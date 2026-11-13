# imgzip-fn-pc

Windows 上的图片批量压缩工具，双引擎：**本机 PC** 与 **fnOS / 类 Linux NAS（SSH 远程）**。右键即可压缩整个文件夹或单张图片，压缩任务默认跑在任务来源所在的一侧。

底层压缩内核为 [caesium-clt](https://github.com/Lymphatus/caesium-clt)（libcaesium，支持 JPEG/PNG/WebP/GIF）。界面为 WinUI 3 + C#（.NET 10），随安装包自带 PC 引擎与运行依赖。

当前源码版本 **0.5.0**：一个窗口只做一件事。每个窗口承载一个压缩任务，任务结束后草稿区收起，想再压一批就再开一个窗口；窗口按 32px 级联排布，关闭某个窗口只结束它自己的任务，其他窗口继续。任务卡片刻画自己在等什么（NAS 串行 / PC 已达并发上限 / 前面还有排队者）。新增「默认引擎」设置（自动推断 / 固定本机 PC / 记住上次切换），并在回落时说明原因。崩溃残留的半成品暂存目录与任务目录会在启动时按归属清理。

0.4.0 起：点 X 直接强制关闭，不再询问；关闭时终止运行中任务的执行层进程树（含引擎子进程）、清理暂存目录并写入终态；杀不掉时保留记录与进程身份，下次启动按 PID 自动定论。详见 [一窗一任务](docs/FEATURES-one-task-per-window.md)、[强制关闭设计](docs/FEATURES-force-close.md) 与 [任务目录回收](docs/FEATURES-job-directory-reclaim.md)。

## 功能

- 双引擎：PC 引擎随包部署；NAS 引擎经 SSH 在 NAS 侧执行
- 右键菜单：文件、文件夹、文件夹空白处（Win11 需点“显示更多选项”）
- 预设：收藏 JPEG（q90 / 长边 4000）、收藏 WebP、分享 WebP（q80 / 2560）
- 高级设置：质量、格式、缩放方式与尺寸、目标体积、线程、无损、不放大、递归、预演
- 其他文件归档：文件夹任务可选择复制未参与压缩的普通文件，递归时保留目录层级
- 输出到 `<源目录>_compressed`（已存在则 `_compressed_2`…），不覆盖原图或旧结果
- 单实例运行；准备阶段不显示虚构百分比，进度来自真实完成事件
- **一窗一任务**：每个窗口一个任务（草稿或结果），窗口单次使用；关闭某窗口只结束它自己的任务
- 多窗口并行：PC 任务按 CPU 核数并发（设置可调 1–64），NAS 任务串行但可与 PC 并行；每窗口显示自己的进度并可取消/重查
- 排队可见：窗口说明自己在等 NAS 任务、PC 名额，还是前面还有排队者
- 引擎并行：PC 侧组批调用消除逐文件进程开销；NAS 侧并发独立设置（默认 2），永远不超过 NAS 核数与 4 的硬上限
- 默认引擎可选：自动推断 / 固定本机 PC / 记住上次切换；回落时说明原因
- 支持取消、失败详情、任务状态未确认时的重新检查

## 安装

安装包构建后位于 `artifacts/installer/ImgZip-<版本>-win-x64-setup.exe`，安装到 `%LOCALAPPDATA%\Programs\ImgZip`，当前用户权限即可，无需管理员；安装时可勾选添加右键菜单。

## 构建

前置条件（Windows 11 x64）：

1. .NET SDK **10.0.400**（由根目录 `global.json` 锁定）
2. PowerShell 7
3. Inno Setup **7.1.0**，或用 `-BootstrapCompiler` 让脚本下载校验固定版本；也可用 `-CompilerPath` 指向已有的 `ISCC.exe`

```powershell
# 已安装 Inno Setup 7.1.0（在 PATH 或默认安装位置）
pwsh -NoProfile -File build/windows.ps1

# 或由脚本下载编译器
pwsh -NoProfile -File build/windows.ps1 -BootstrapCompiler

# 或用已有编译器（脚本不会重复下载）
pwsh -NoProfile -File build/windows.ps1 -CompilerPath .tools/inno/ISCC.exe

# 只想跳过非 UI 测试
pwsh -NoProfile -File build/windows.ps1 -CompilerPath .tools/inno/ISCC.exe -SkipTests
```

脚本依次运行测试、锁定模式还原、目录形式自包含发布、下载并校验 caesium-clt 1.4.0、检查引擎可启动、编译安装程序。细节与产物路径见 [Windows 说明](docs/WINDOWS.md)。

## 首次配置

应用启动后进入设置，填写 NAS 连接信息：

- `server`：NAS IP 或主机名
- `user`：SSH 用户名（**区分大小写**，例如 `Why` 与 `why` 是不同账号）
- `port`：SSH 端口，默认 22
- `keyPath`：本机私钥路径，应用只保存路径，不复制私钥
- `nasHosts` / `nasIp`：用于识别共享是否属于本 NAS，从而决定是否提供 NAS 引擎

配置保存在 `%LOCALAPPDATA%\ImgZip\local-config.json`，可在设置中“测试连接”验证后再保存；需要口令的私钥请先用 SSH agent 解锁。NAS 并发作业数可在设置中调整为 1–4，实际值会按探测到的 NAS 核数再次夹取。

## 使用

右键文件夹或图片选择“使用 ImgZip 压缩图片”，或直接启动应用后拖入/选择来源。也可以从命令行传入来源：

```powershell
& "$env:LOCALAPPDATA\Programs\ImgZip\ImgZip.exe" --path 'D:\照片\旅行'
& "$env:LOCALAPPDATA\Programs\ImgZip\ImgZip.exe" --path '\\NAS\photos\旅行'
```

应用按用户保持单实例，右键再次启动会新开一个窗口承载这批来源（不会顶掉已有窗口里的草稿）。每个窗口只做一个任务；任务运行期间仍可调整该窗口的草稿，但已提交任务使用独立参数快照，草稿修改不会改变它。任务结束后草稿区收起，窗口只保留结果与「打开输出」，要再压一批请再开一个窗口。不带参数再次双击只会把已有的空白窗口叫到前面，不会堆出一排空窗口。

「复制其他文件」只对选择单个文件夹时生效。勾选后，图片仍按预设压缩；其他普通文件在图片处理完成后复制到同一输出目录。该选项包括隐藏/系统文件和 `Thumbs.db`，但跳过符号链接、重解析点和空目录。列表中的勾选状态会保存在本机配置中。

排队中的窗口会说明自己在等谁：NAS 串行时显示「NAS 已有任务在执行」，PC 达到并发上限时显示「已有 N 个 PC 任务在跑（上限 M）」，前面还有更早的排队者时也会说明。名额一空出，提示立即消失并自动开始。

排队任务可「移除」，移除后不会再调用压缩引擎。尚未启动的任务重开后显示「等待手动继续」，可「继续」或「移除」，不会自动压缩。进程已确认退出的任务在重开时自动判定中断并释放名额，无需人工重新检查；它留下的半成品暂存目录也会按归属清理。关闭窗口（点 X）只强制关闭**该窗口自己的任务**：终止其执行层进程树、清理暂存目录并写入终态，不弹对话框；其他窗口的任务不受影响。无法确认停止时保留记录与进程身份，下次启动按进程状态自动定论。

## NAS 引擎准备

应用不会自动安装 NAS 侧引擎。请按 [caesium-clt 1.4.0 官方发布](https://github.com/Lymphatus/caesium-clt/releases/tag/v1.4.0) 选择适合 NAS 架构的 Linux 版本，部署到 `~/bin/caesiumclt` 并赋予执行权限。

NAS 侧需要 Bash 4+、GNU find/stat/coreutils、util-linux 的 setsid、ps/awk，并允许读取 Samba 共享配置。SSH 使用 Windows OpenSSH 客户端与公钥认证；首次连接会以 `accept-new` 记录主机密钥，主机密钥变化时拒绝连接。

## 安全说明

- 私钥始终只存在本机 `~/.ssh`，应用仅保存路径
- 配置与日志位于 `%LOCALAPPDATA%\ImgZip`；任务记录包含处理参数与来源路径，不包含私钥内容
- 默认输出到新目录，原图不动；卸载不删除配置、任务记录或压缩结果

## 文档

- [Windows 构建与环境](docs/WINDOWS.md)
- [协议说明](docs/PROTOCOL.md)
- [检查与验收](docs/VALIDATION.md)
- [界面设计](docs/DESIGN-A.md)
- [0.3.6 其他文件复制](docs/FEATURES-0.3.6-copy-other-files.md)
- [强制关闭：点 X 直接终止执行层](docs/FEATURES-force-close.md)
- [任务目录回收](docs/FEATURES-job-directory-reclaim.md)
- [引擎默认选择：自动推断 / 固定本机 / 记住上次](docs/FEATURES-engine-mode-selection.md)
- [一窗一任务与新建任务置前](docs/FEATURES-one-task-per-window.md)
- [0.3.5 NAS 并发事故与修复](docs/UI-ISSUES-0.3.5-nas-concurrency.md)
- [0.5.0 后续问题记录（已记录，未实施）](docs/UI-ISSUES-0.5.0-followup.md)
- [0.3.4 修复记录](docs/UI-ISSUES-0.3.3-0.3.4.md)
- 界面方案演示（HTML，本地打开）：[design-demo](design-demo/README.md)
