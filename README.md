# imgzip-fn-pc

图片批量压缩工具：**Windows 本地(PC) 与 fnOS/类 Linux NAS 双端引擎**，右键即可压缩文件夹或单张图片。

- PC 引擎：本机 `bin\caesiumclt.exe` 直接压缩本地/共享路径
- NAS 引擎：SSH 遥控 NAS 上的 `caesiumclt`（默认引擎，压缩任务跑在 NAS 侧）
- 自动识别路径归属：本地盘 / 映射盘符 / UNC 共享，识别出本 NAS 才提供 NAS 引擎
- 单窗体完成「选择引擎 → 实时进度 → 结果摘要」，无黑窗无闪烁

底层压缩内核为 [caesium-clt](https://github.com/Lymphatus/caesium-clt)（libcaesium，支持 JPEG/PNG/WebP/GIF）。

## A 版原生应用与安装包构建

已选定 A「轻量工具窗」，新版源码位于 `src/ImgZip.App`（WinUI 3 + C#）与 `src/ImgZip.Core`（独立逻辑类库），无界面双引擎入口位于 `worker/`。使用 .NET 10、指定灰紫浅深主题、单任务实际进度与新输出目录保护。原有脚本入口保持可用。

Windows 11 x64 构建：`pwsh -File build/windows.ps1 -BootstrapCompiler`。安装到当前用户目录，随包部署运行依赖和固定版本 caesium，支持可选右键菜单。完整环境、配置迁移、构建和使用步骤见 [Windows 说明](docs/WINDOWS.md)，实际验证范围见 [检查与验收](docs/VALIDATION.md)。**当前交付源码及可复现流程，尚未在 Windows 构建安装包或完成运行验证。**

## Windows 11 界面设计演示

新版界面的三个 HTML 方案位于 [`design-demo/`](design-demo/README.md)。用浏览器打开 [`design-demo/index.html`](design-demo/index.html) 即可比较轻量工具窗、Windows 设置风格和双栏工作台，支持指定的灰紫配色、明暗主题与模拟交互，无需安装依赖。

这些演示仅保留作设计参考，压缩与 NAS 连接均为模拟；原生只实现已选定的 A 版。以下功能、目录结构和快速开始仍是原有 PowerShell 版本的使用说明，新版请阅读上方 Windows 说明。

## 功能

- 批量/递归或“仅本文件夹”压缩，保留目录结构、EXIF、文件时间
- 预设：`archive-jpeg`（长边 4000 + q90 + JPEG，收藏默认）、`archive-webp`、`webp-share`
- 灵活参数：质量、长边/短边/固定宽高缩放、目标体积、不放大、无损、预演
- 纯 JPEG 源自动跳过“同格式转换”，避免 caesiumclt 报错
- 进度按 Windows 侧统计输出目录已生成文件数，SMB 不可用时自动降级动效

## 目录结构

```text
imgzip-fn-pc/
├─ imgzip-sendto.ps1      # 右键入口：路径识别 + 引擎分发
├─ imgzip-remote.ps1      # 执行器：NAS/PC 双引擎 + 单窗体
├─ install-sendto.ps1     # 安装/卸载右键菜单（生成无窗口启动器）
├─ local-config.example.json  # 配置模板（复制为 local-config.json 填写）
├─ local-config.json      # 本机配置（已在 .gitignore，不会上传）
└─ .gitignore             # 隔离本机 IP/账号、密钥、日志与大文件
```

## 快速开始

### 1. 本机配置

复制配置模板并按需填写（NAS 的 IP、SSH 用户名、主机名识别列表）：

```powershell
Copy-Item local-config.example.json local-config.json
# 编辑 local-config.json：server/user/nasHosts/nasIp/keyPath
```

`local-config.json` 已在 `.gitignore` 中，**不会**随仓库上传。未配置时，右键 NAS 共享只提供 PC 引擎（仍可用 SMB 压缩），配置后才会出现 NAS 引擎选项。

### 2. 安装右键菜单

```powershell
.\install-sendto.ps1              # 安装（HKCU，无需管理员）
.\install-sendto.ps1 -Uninstall   # 卸载
```

右键「压缩图片（NAS/PC）」出现在文件夹图标、文件、文件夹空白处（Win11 需先点“显示更多选项”）。

### 3. 准备引擎

- **PC 引擎**：从 [caesium-clt Releases](https://github.com/Lymphatus/caesium-clt/releases) 下载 `x86_64-pc-windows-msvc.zip`，解压 `caesiumclt.exe` 到项目 `bin\` 目录。
- **NAS 引擎**（任选其一）：
  - NAS 能访问 GitHub：`.\imgzip-remote.ps1 -Setup`（下载到 NAS `~/bin/caesiumclt`）
  - 或 PC 下载 Linux 版 `x86_64-unknown-linux-musl.tar.gz` 后经 `scp` 推送到 NAS 并解压到 `~/bin/caesiumclt`

## 使用

### 右键

1. 资源管理器打开本地文件夹或 NAS 共享，右键要压缩的文件夹/图片
2. 点「压缩图片（NAS/PC）」→ 弹出单窗体
3. 选择引擎（NAS 路径默认 NAS，可切 PC；本地路径仅 PC）、勾选“仅处理本文件夹内的图片”
4. 点 [开始压缩]，进度条显示 `已完成 x / y`，结束原地显示结果摘要

输出到 `<源目录>_compressed`，**原图不动**；右键单个图片只压缩该图片。

### 命令行

```powershell
# NAS 引擎（配合 local-config.json 的连接信息）
.\imgzip-remote.ps1 -Source /vol1/photo/写真 -Preset archive-jpeg -DryRun
.\imgzip-remote.ps1 -Source /vol1/photo/写真 -Preset archive-jpeg

# PC 引擎（Windows 路径，可 -Recurse 递归）
.\imgzip-remote.ps1 -Local -Source D:\photo\写真 -Preset archive-jpeg -DryRun
.\imgzip-remote.ps1 -Local -Source D:\photo\写真 -Preset archive-webp -Recurse

# 经 sendto 自动识别后分发（等价右键）
.\imgzip-sendto.ps1 -Path '\\SERVER\SHARE\写真' -Yes -DryRun
```

## 参数速查

| 参数 | 说明 | 默认 |
|---|---|---|
| `-Source` | NAS 真实路径；`-Local` 时为本机/共享路径 | 必填 |
| `-Output` | 输出目录 | `<源目录>_compressed` |
| `-Preset` | `archive-jpeg` / `archive-webp` / `webp-share` | `none` |
| `-Quality` | 有损质量 0-100 | 82 |
| `-Format` | `keep`/`webp`/`jpeg`/`png` | `keep` |
| `-LongEdge / -ShortEdge / -Width / -Height` | 等比缩放像素，0=不缩放 | 0 |
| `-MaxSize` | 目标最大体积（字节） | 0 |
| `-NoUpscale` | 缩放时不放大小图 | 关 |
| `-Lossless` | 无损模式 | 关 |
| `-Recurse` | 目录压缩递归进入子文件夹 | 关（仅本文件夹） |
| `-DryRun` | 只预演不写文件 | 关 |
| `-Local` | 使用本机 PC 引擎 | 关 |
| `-Setup` / `-Test` | 在 NAS 上安装 caesiumclt / 测试连接 | 关 |

## 安全说明

- SSH 私钥只存在本机 `~/.ssh`；`local-config.json`、`smb-map.json`、`sendto-log.txt` 均被 `.gitignore` 排除，**不会上传到仓库**
- 默认输出到新目录 `_compressed`，原图不动；确认效果后再自行处理原图
- 压缩默认按范围统计与输出；若遇 0 图片范围会提示“没有可压缩图片”而非报错
