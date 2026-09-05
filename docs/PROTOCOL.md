# 执行协议 v1

`ImgZip.Core` 不引用 WinUI；窗口及系统文件选择器在 `ImgZip.App`，状态/参数在 `MainViewModel`，配置在 `ConfigStore`，进程通信在 `WorkerClient`。`worker/imgzip-worker.ps1` 为独立无界面入口；`nas-worker.sh` 是固定远程脚本。原有三个 PowerShell 脚本保持兼容、独立可用，新入口沿用其预设、caesium 参数和共享解析语义。

每次请求启动一个 PowerShell 子进程，标准输入发送一行 UTF-8 JSON 后关闭输入。标准输出仅为逐行 JSON 事件；标准错误为诊断。Windows PowerShell 5.1 脚本使用 UTF-8 BOM 以正确读取中文代码文字，通信正文为 UTF-8。命令行参数通过 .NET ArgumentList/Windows 引号编码传递。

```json
{"version":1,"operation":"run","jobId":"11111111111111111111111111111111","engine":"pc","sources":["D:\\照片\\旅行"],"options":{"preset":"archive-jpeg","format":"jpeg","quality":90,"resize":"long","pixels":4000,"maxSize":0,"threads":4,"lossless":false,"noUpscale":true,"recurse":false,"dryRun":true},"config":{"server":"","user":"","nasHosts":[],"nasIp":"","keyPath":"","port":22,"theme":"system"},"enginePath":"C:\\Users\\user\\AppData\\Local\\Programs\\ImgZip\\bin\\caesiumclt.exe","sshPath":"ssh","stateDirectory":"C:\\Users\\user\\AppData\\Local\\ImgZip"}
```

`jobId` 为 32 位小写十六进制 GUID，恢复/取消必须复用原请求的 ID、引擎与配置。`sshPath` 仅供测试注入替身，应用固定使用 `ssh`。接口不是网络服务或不可信请求沙箱。

| operation | 行为 |
| --- | --- |
| probe | 探测 PC 引擎或 SSH/远程引擎与工具；不执行任务 |
| resolve | Windows 源校验、映射盘/UNC → NAS 路径解析 |
| run | 建立文件清单、规划新输出目录及冲突文件名，然后逐文件处理 |
| cancel | 独立控制进程请求同 ID 任务停止；请求接受不等于停止确认 |
| inspect | 读取同 ID 的持久事件，无法确认终态则返回 unknown |

事件均包含 `version/jobId/type`。`phase` 表示准备阶段；`manifest` 给出本次清单总数及输出位置；`file` 给出 `index/path/state` 和累计完成/成功/失败/跳过数。`file.beforeBytes/afterBytes` 是该成功文件的配对字节数，失败文件为 0。`finished` 的字节数仅汇总成功文件。`output` 为实际执行端目录，NAS 可另提供经共享映射得到的 `openOutput`。

`finished.state` 为 `succeeded`、`partial`、`failed`、`cancelled`、`dryRun`、`empty` 或 `unknown`。`unknown` 是观察结果，不能解锁或宣称远端停止。`cancelRequested` 只确认取消请求已送达。`probe` 使用 `pcAvailable` 或 `connected/nasAvailable` 分别表达状态。

NAS 字符串在内部事件中用 `path64/output64/message64` 编码，PowerShell 解码后再交给 C#。SSH 只执行固定 `bash -s`；所有动态来源、共享名、参数经 Base64 赋值传入固定脚本，不作为 Bash 源码插值。路径清单使用 NUL 分隔枚举、引号包裹数组参数。

NAS 任务启动到独立 setsid 会话，SSH 连接只读取任务日志。取消校验远端 `/proc` 启动时间和会话 ID，不因 PID 重用误杀其他任务；没有确认日志则返回 unknown。PC 取消通过用户数据目录中的任务取消标志，由运行进程终止自己的引擎子进程树并确认退出。

客户端拒绝身份/版本错误、未知事件、非法计数、缺失终态、非 JSON 输出及终态之后追加事件。异常流会被排空以避免管道死锁，并报告协议异常。已确认任务终态不会被并发的取消/重查观察结果覆盖。
