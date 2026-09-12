# ImgZip A · Windows 11 x64

应用采用 WinUI 3 + C# 与 A「轻量工具窗」。**0.3.4** 修复任务调度与恢复，保留 0.3.3 的界面和性能优化。0.3.3 已有用户 Windows 实测记录；本次 0.3.4 仅完成 Mac 非视觉检查，尚未在 Windows 编译 XAML、生成安装程序或验证真实压缩。请完成本版本的 [验收清单](VALIDATION.md)。

## 构建安装包

在 Windows 11 x64 上准备：

1. .NET SDK **10.0.400**（由根目录 `global.json` 锁定）。
2. PowerShell 7，用于运行构建脚本。应用运行时使用 Windows 内置 PowerShell 5.1。
3. Inno Setup **7.1.0**。可手动安装，也可明确运行下方 `-BootstrapCompiler`，由脚本下载并校验固定版本编译器。
4. 首次构建需要访问 NuGet 和 GitHub 下载依赖。使用 Visual Studio 时，选择支持 .NET 10 的 Visual Studio 2026 与 WinUI 工作负载；命令行流程依据 [微软 WinUI 工具链](https://learn.microsoft.com/en-us/windows/apps/get-started/winui-get-started-overview)。

在仓库根目录执行：

```powershell
# 已安装 Inno Setup 7.1.0
pwsh -NoProfile -File build/windows.ps1

# 或：下载并校验编译器，放入项目 .tools/inno
pwsh -NoProfile -File build/windows.ps1 -BootstrapCompiler

# 编译器位于其他目录
pwsh -NoProfile -File build/windows.ps1 -CompilerPath 'D:\Tools\Inno Setup 7\ISCC.exe'
```

脚本依次运行非 UI 测试、锁定模式还原、目录形式自包含发布、下载并校验 caesium-clt 1.4.0、检查引擎可启动、编译安装程序。版本取自 `Directory.Build.props`，当前产物路径：

```text
artifacts/installer/ImgZip-0.3.4-win-x64-setup.exe
artifacts/installer/ImgZip-0.3.4-win-x64-setup.exe.sha256
artifacts/build/<本次构建ID>/publish/
```

若只需要发布目录：

```powershell
dotnet restore src/ImgZip.App/ImgZip.App.csproj --locked-mode
dotnet publish src/ImgZip.App/ImgZip.App.csproj -c Release --no-restore -o artifacts/publish
```

上面两条不下载 caesium；完整发布请用 `build/windows.ps1`。不要额外传全局 `-r`：应用项目已固定 `win-x64`，额外参数会改变核心类库的跨平台锁定图。发布设置同时启用 `SelfContained` 和 `WindowsAppSDKSelfContained`，不裁剪、不合并单文件，依据 [微软自包含部署说明](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)。

`.github/workflows/windows-installer.yml` 只接受 `workflow_dispatch`。源码推送后，用户可自行到 Actions → Build Windows installer → Run workflow，完成后下载 artifact。此工作流没有 push/PR 自动触发器，没有发布 Release 的权限。0.3.4 默认只提交源码，不创建 Release、不触发远程构建。

## 安装、升级与卸载

安装到 `%LOCALAPPDATA%\Programs\ImgZip`，使用 `PrivilegesRequired=lowest`，无需管理员权限；参见 [Inno Setup 权限说明](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)。安装创建当前用户的开始菜单入口与卸载入口，可选添加文件、文件夹及文件夹空白处的“使用 ImgZip 压缩图片”菜单，位于 Windows 11“显示更多选项”。

新版菜单独立使用 `ImgZipWinUI` 注册表键；早期版本的 `ImgZipCompress` 键如仍存在，需由用户自行清理。升级前请先结束任务并关闭应用，再运行新版安装程序。升级与卸载均保留 `%LOCALAPPDATA%\ImgZip`；卸载不删除私钥或压缩结果。安装包默认不签名，正式分发如需代码签名，应由发布者在自己的 Windows 构建环境配置证书。

## 使用

独立启动为空状态；添加一个文件夹，或同目录多张图片。支持拖入、系统文件/文件夹选择器，以及：

```powershell
& "$env:LOCALAPPDATA\Programs\ImgZip\ImgZip.exe" --path 'D:\照片\旅行'
& "$env:LOCALAPPDATA\Programs\ImgZip\ImgZip.exe" --path '\\NAS\photos\旅行'
```

应用按用户保持单实例。再次从右键启动会把来源交给现有窗口的草稿区；运行中也可添加来源、选择预设和引擎。每次提交保存独立的参数快照，后续草稿修改不影响已提交任务；重复来源不会重复入队。

默认收藏 JPEG（q90、长边 4000）、不递归、不放大小图。收藏 WebP 为 q90/4000；分享 WebP 为 q80/2560。高级设置支持质量、格式、缩放、目标字节数、线程、无损、不放大、递归和预演。无损禁用质量及目标体积，不缩放时不传尺寸参数。线程数是引擎并行作业数：PC 侧按批调用引擎，NAS 侧按该值并发执行单文件作业；多任务并行时按活跃任务数自动分配。

多任务：PC 任务并发上限取自设置的“PC 并行任务数”（0 = 自动 = CPU 核数，可调 1–64）；NAS 任务至多一个，但可与 PC 任务并行。已启动但状态未知的任务继续占用对应名额，确认结束后才释放。主界面底部逐条显示进度/当前文件，可取消、重新检查、打开输出与查看失败详情；成功的任务自动移出列表，失败/取消/待确认保留。排队任务可“移除”，移除会终止等待，不会在其他任务结束后偷偷启动。

任务记录写入 `tasks/<jobId>.json`，本地记录格式为 v2，含请求快照与 `queued / launching / running / finished` 阶段。重启后，明确尚未启动的 `queued` 显示“等待手动继续”，提供“继续”和“移除”，不自动压缩；`launching/running` 待检查并占用名额；`finished` 按已保存终态收尾。启动前无法保存 `launching` 时不会调用引擎。

旧 `tasks/*.json` 和 `active-job.json` 按任务 ID 合并迁移。旧记录无法可靠证明未启动，因此保守恢复为待检查，不凭事件日志缺失判断可以重跑。迁移成功后才清理旧入口；损坏或迁移失败会提示并保留原文件。

输出为 `<源目录>_compressed`；单文件或多文件使用其父目录名。已有目录时使用 `_compressed_2` 等新目录，同一任务内转换重名和大小写冲突使用编号文件名。最终输出提交仍检查占用，不覆盖原图或旧结果。预演只建立清单并校验参数，不创建输出目录或运行压缩引擎，不预测压缩比例。日志和任务记录仍会写入用户数据目录。

进度来自完成事件；准备时不显示虚构百分比。摘要只统计本次任务，成功项的源字节与实际输出字节配对计算。部分失败保留成功产物，可打开输出位置、查看失败详情。取消保留已完成产物。

## 配置与 NAS

配置位于 `%LOCALAPPDATA%\ImgZip\local-config.json`。保留 `server`、`user`、`nasHosts`、`nasIp`、`keyPath`，新增 `port=22`、`theme="system"`。首次启动不自动读取旧项目配置，可在设置中“导入旧配置”再“保存”；导入不修改原文件。SSH 只保存私钥路径，不复制私钥。

主题跟随系统，选择浅色/深色会立即写入 `theme`。NAS 连接字段仅在点击“保存”后提交，“测试连接”只测试当前输入。连接测试区分 SSH 连通和 NAS 引擎/必要工具可用。

PC 引擎随安装包部署。NAS 使用 Windows OpenSSH 客户端及公钥认证；服务器需安装 Bash 4+、GNU find/stat/coreutils、util-linux 的 setsid、ps/awk，并允许读取 Samba 共享配置。引擎位置为 `~/bin/caesiumclt`。首次连接使用 OpenSSH `accept-new` 保存主机密钥，已有主机密钥改变时拒绝连接。带口令的私钥需通过用户的 SSH agent 解锁；应用不收集口令。

NAS 侧引擎需自行部署：按 [caesium-clt 1.4.0 官方发布](https://github.com/Lymphatus/caesium-clt/releases/tag/v1.4.0) 选择适合 NAS 架构的 Linux 版本，放入 `~/bin/caesiumclt` 并赋予执行权限。新版应用不会自动安装或升级 NAS 侧引擎。

映射盘通过 Windows CIM 转成 UNC，验证共享主机归属后重新读取 Samba 共享到真实目录的映射；不盲信缓存。解析或 SSH 失败会显示原因。Windows 可以访问来源且 PC 引擎可用时，可以手动切换本机处理。

## 取消与故障记录

取消先显示“正在取消”。PC 执行层终止本任务引擎进程树并等待退出；NAS 使用独立会话/进程组，校验 PID、启动时间和会话身份后取消。SSH 断开本身不代表远程任务停止。

无法确认时显示“状态待确认”，保留任务名额及“重新检查”。草稿仍可编辑，其他任务按剩余名额执行。关闭窗口会检查全部任务：“取消并等待”先停止所有排队等待，再取消运行任务、重查未知任务；仍无法确认结束时保留窗口。“仅关闭窗口”不保证任务停止，下次启动按保存阶段恢复。远程进程异常退出、任务记录丢失或损坏时，可能仍无法自动证明终态；需要用户检查实际进程和输出，不能通过删除记录假定已取消。

本机诊断在 `%LOCALAPPDATA%\ImgZip\logs`，任务事件在 `jobs/<任务ID>/events.jsonl`，共享缓存为 `smb-map.json`。NAS 任务记录在 `~/.cache/imgzip/jobs/<任务ID>/`。请求不写入诊断日志，NAS 任务记录包含处理参数与来源路径，不包含私钥内容。请保留异常任务记录用于排查。

技术协议见 [PROTOCOL.md](PROTOCOL.md)，实际检查范围见 [VALIDATION.md](VALIDATION.md)，界面规范见 [DESIGN-A.md](DESIGN-A.md)。
