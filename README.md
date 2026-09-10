# imgzip-fn-pc

Windows 上的图片批量压缩工具，双引擎：**本机 PC** 与 **fnOS / 类 Linux NAS（SSH 远程）**。右键即可压缩整个文件夹或单张图片，压缩任务默认跑在任务来源所在的一侧。

底层压缩内核为 [caesium-clt](https://github.com/Lymphatus/caesium-clt)（libcaesium，支持 JPEG/PNG/WebP/GIF）。界面为 WinUI 3 + C#（.NET 10），随安装包自带 PC 引擎与运行依赖。

## 功能

- 双引擎：PC 引擎随包部署；NAS 引擎经 SSH 在 NAS 侧执行
- 右键菜单：文件、文件夹、文件夹空白处（Win11 需点“显示更多选项”）
- 预设：收藏 JPEG（q90 / 长边 4000）、收藏 WebP、分享 WebP（q80 / 2560）
- 高级设置：质量、格式、缩放方式与尺寸、目标体积、线程、无损、不放大、递归、预演
- 输出到 `<源目录>_compressed`（已存在则 `_compressed_2`…），不覆盖原图或旧结果
- 单实例运行；准备阶段不显示虚构百分比，进度来自真实完成事件
- 支持取消、失败详情、任务状态未确认时的重新检查

## 安装

安装包构建后位于 `artifacts/installer/ImgZip-<版本>-win-x64-setup.exe`，安装到 `%LOCALAPPDATA%\Programs\ImgZip`，当前用户权限即可，无需管理员；安装时可勾选添加右键菜单。

## 构建

前置条件（Windows 11 x64）：

1. .NET SDK **10.0.400**（由根目录 `global.json` 锁定）
2. PowerShell 7
3. Inno Setup **7.1.0**，或用 `-BootstrapCompiler` 让脚本下载校验固定版本

```powershell
# 已安装 Inno Setup 7.1.0
pwsh -NoProfile -File build/windows.ps1

# 或由脚本下载编译器
pwsh -NoProfile -File build/windows.ps1 -BootstrapCompiler

# 只想跳过非 UI 测试
pwsh -NoProfile -File build/windows.ps1 -BootstrapCompiler -SkipTests
```

脚本依次运行测试、锁定模式还原、目录形式自包含发布、下载并校验 caesium-clt 1.4.0、检查引擎可启动、编译安装程序。细节与产物路径见 [Windows 说明](docs/WINDOWS.md)。

## 首次配置

应用启动后进入设置，填写 NAS 连接信息：

- `server`：NAS IP 或主机名
- `user`：SSH 用户名（**区分大小写**，例如 `Why` 与 `why` 是不同账号）
- `port`：SSH 端口，默认 22
- `keyPath`：本机私钥路径，应用只保存路径，不复制私钥
- `nasHosts` / `nasIp`：用于识别共享是否属于本 NAS，从而决定是否提供 NAS 引擎

配置保存在 `%LOCALAPPDATA%\ImgZip\local-config.json`，可在设置中“测试连接”验证后再保存；需要口令的私钥请先用 SSH agent 解锁。

## 使用

右键文件夹或图片选择“使用 ImgZip 压缩图片”，或直接启动应用后拖入/选择来源。也可以从命令行传入来源：

```powershell
& "$env:LOCALAPPDATA\Programs\ImgZip\ImgZip.exe" --path 'D:\照片\旅行'
& "$env:LOCALAPPDATA\Programs\ImgZip\ImgZip.exe" --path '\\NAS\photos\旅行'
```

应用按用户保持单实例：任务执行中或状态未知时，新的来源会被拒绝，不会覆盖当前任务。

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
- 界面方案演示（HTML，本地打开）：[design-demo](design-demo/README.md)
