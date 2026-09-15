# 执行协议 v1

`ImgZip.Core` 不引用 WinUI；窗口及系统文件选择器在 `ImgZip.App`，草稿参数在 `MainViewModel`，任务生命周期在其 `MainViewModel.Tasks.cs` 部分，配置在 `ConfigStore`，本地任务记录在 `TaskStore`，进程通信在 `WorkerClient`。`worker/imgzip-worker.ps1` 为独立无界面入口；`nas-worker.sh` 是固定远程脚本。当前仓库使用上述执行入口，沿用原脚本的预设、caesium 参数和共享解析语义。

每次请求启动一个 PowerShell 子进程，标准输入发送一行 UTF-8 JSON 后关闭输入。标准输出仅为逐行 JSON 事件；标准错误为诊断。Windows PowerShell 5.1 脚本使用 UTF-8 BOM 以正确读取中文代码文字，通信正文为 UTF-8。命令行参数通过 .NET ArgumentList/Windows 引号编码传递。

```json
{"version":1,"operation":"run","jobId":"11111111111111111111111111111111","engine":"pc","sources":["D:\\照片\\旅行"],"options":{"preset":"archive-jpeg","format":"jpeg","quality":90,"resize":"long","pixels":4000,"maxSize":0,"threads":4,"lossless":false,"noUpscale":true,"recurse":false,"copySkippedFiles":false,"dryRun":true},"config":{"server":"","user":"","nasHosts":[],"nasIp":"","keyPath":"","port":22,"theme":"system","pcConcurrency":0,"nasConcurrency":2,"copySkippedFiles":false},"enginePath":"C:\\Users\\user\\AppData\\Local\\Programs\\ImgZip\\bin\\caesiumclt.exe","sshPath":"ssh","stateDirectory":"C:\\Users\\user\\AppData\\Local\\ImgZip"}
```

`jobId` 为 32 位小写十六进制 GUID，恢复/取消必须复用原请求的 ID、引擎与配置。`sshPath` 仅供测试注入替身，应用固定使用 `ssh`。接口不是网络服务或不可信请求沙箱。`options.threads` 表示该任务请求的引擎并发作业数；NAS worker 仍会夹取到 `min(请求值, nproc, 4)`，并按可用内存进一步降低新增并发。

| operation | 行为 |
| --- | --- |
| probe | 探测 PC 引擎或 SSH/远程引擎与工具；不执行任务 |
| resolve | Windows 源校验、映射盘/UNC → NAS 路径解析 |
| run | 建立图片与其他文件清单、规划新输出目录及冲突文件名；PC 组批调用，NAS 并发单文件作业，统一逐文件/复制事件 |
| cancel | 独立控制进程请求同 ID 任务停止；请求接受不等于停止确认 |
| inspect | 读取同 ID 的持久事件，无法确认终态则返回 unknown |

事件均包含 `version/jobId/type`。`phase` 表示准备阶段；`manifest` 给出图片总数、其他文件复制数和输出位置；`file` 给出图片进度；`copy` 给出其他文件的 `copied/failed` 进度。累计复制状态使用 `copyTotal/copyCompleted/copied/copyFailed/copiedBytes`。`file.beforeBytes/afterBytes` 是成功图片的配对字节数；`copy.copiedBytes` 只汇总复制成功的其他文件。`output` 为实际执行端目录，NAS 可另提供经共享映射得到的 `openOutput`。

`finished.state` 为 `succeeded`、`partial`、`failed`、`cancelled`、`dryRun`、`empty` 或 `unknown`。`unknown` 是观察结果，不能释放该任务的执行名额或宣称远端停止。`cancelRequested` 只确认取消请求已送达。`probe` 使用 `pcAvailable` 或 `connected/nasAvailable` 分别表达状态；NAS 探测同时返回 `nasCores` 与 `nasMemoryMb`，应用用前者夹取 NAS 并发设置。PC 的可打开路径取 `output`，NAS 取映射后的 `openOutput`。

NAS 字符串在内部事件中用 `path64/output64/message64` 编码，PowerShell 解码后再交给 C#。SSH 只执行固定 `bash -s`；所有动态来源、共享名、参数经 Base64 赋值传入固定脚本，不作为 Bash 源码插值。路径清单使用 NUL 分隔枚举、引号包裹数组参数。

NAS 任务启动到独立 setsid 会话，SSH 连接只读取任务日志。取消校验远端 `/proc` 启动时间和会话 ID，不因 PID 重用误杀其他任务；没有确认日志则返回 unknown。PC 取消通过用户数据目录中的任务取消标志，由运行进程终止自己的引擎子进程树并确认退出。

当 `copySkippedFiles=true` 且来源是单个文件夹时，普通非图片文件会在图片压缩后复制。PC 与 NAS 均跳过符号链接/重解析点，不复制空目录；递归模式保留相对目录结构。文件内容和时间/基础属性通过临时文件完成后原子发布，复制失败进入同一任务的失败详情并影响 `partial/failed` 状态。

NAS worker 在创建输出目录后记录 `requested / nas_cores / effective / cap`，每次启动子任务前记录当前并发数与 `MemAvailable`。低于 1.5 GB 时最多新增 1 个，低于 3 GB 时最多新增 2 个；引擎通过 `nice -n 10` 启动，避免压缩满载时 SSH/SMB 失去响应。

客户端拒绝身份/版本错误、未知事件、非法计数、缺失终态、非 JSON 输出及终态之后追加事件。异常流会被排空以避免管道死锁，并报告协议异常。已确认任务终态不会被并发的取消/重查观察结果覆盖。

## 本地任务记录 v2（自 0.3.4 起）

此版本号只属于应用本地存储，PowerShell 请求和事件的 `version` 仍为 **1**。

`%LOCALAPPDATA%\ImgZip\tasks/<jobId>.json` 保存 `schemaVersion=2`、完整 `request` 快照、`phase`、`createdAt`、`result` 和 `failures`。`result` 为已确认的最终事件，`failures` 为已收集的失败详情。文件在同一目录写入临时文件后原子替换。

| phase | 写入时机 | 重启行为 |
| --- | --- | --- |
| queued | 用户提交后、等候名额之前 | 等待手动继续，可继续或移除，不自动运行 |
| launching | 保留名额后、调用执行层之前；写入失败则禁止启动 | 待检查，占用原引擎名额 |
| running | 收到准备、清单或文件事件后 | 待检查，占用原引擎名额 |
| finished | 收到可信终态后，包含最终事件与失败详情 | 幂等清理记录，不重复执行 |

任务状态转换、名额分配及持久写入串行进行。已启动的 unknown 任务保留名额；取消排队任务先取消独立等待令牌，获得名额后再检查令牌，避免移除与启动竞争。已确认终态不能被迟到事件覆盖，收尾先保存最终结果再清理；清理失败时显示错误，保留可恢复记录。

旧 `tasks/*.json` 原始请求与 `active-job.json` 按任务 ID 合并，新记录成功落盘后才删除旧 marker。旧记录没有阶段，统一按 `launching` 恢复为待检查，不依据事件日志缺失推断未启动。格式损坏、版本不支持或迁移失败会显示文件路径和错误并保留原文件；有效但迁移写入失败的旧任务仍注册为待检查任务。不要手动删除 unknown 记录来假定任务已停止。
