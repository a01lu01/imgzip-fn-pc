# 检查记录与验收清单

## Mac 已执行的检查

本次环境：macOS arm64；项目内 .NET SDK 10.0.400、PowerShell 7.6.5，不修改系统 PATH。核心与执行层控制台测试共 **12 组通过**，使用真实 PowerShell 入口和替代引擎；未访问 NAS，也未执行真实图像编码。

覆盖配置导入不修改原文件、旧字段缺省值、主题持久化、预设/参数互斥、非法事件解析、未知状态锁定/恢复、预演不创建输出、不递归/递归、中文/空格/引号/美元符/与号路径、同格式参数省略、转换重名、已有目录编号、部分失败/异常引擎、成功文件配对统计、取消后子进程退出及持久事件、畸形/中断协议。NAS 替身覆盖实际 SSH 参数与脚本 Base64 传输、共享映射回 UNC、连通与引擎可用分离、断线/未确认取消保留 unknown、重新检查恢复。

另完成：App 与 Core NuGet 锁定模式还原，C# 窗口代码的引用程序集类型检查，XAML/XML 格式检查、PowerShell 语法解析、Bash `-n`、Git 空白检查。窗口 C# 检查使用临时 XAML 名称字段替身，存在引用版本统一警告，**不等价于 XAML 编译或 Windows 构建成功**。

尝试原生构建时，在 Windows 专用 `XamlCompiler.exe` 处因 Mac 无法执行 Windows 二进制而停止。没有生成 Windows 安装包。NAS Bash 仅做静态语法检查，真实 Linux 编码及远程进程组取消仍待验证。

重新执行 Mac 检查（仓库根目录）：

```sh
python3 build/bootstrap-mac.py
export DOTNET_CLI_HOME="$PWD/.tools/dotnet-home"
export NUGET_PACKAGES="$PWD/.tools/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
.tools/pwsh/pwsh -NoLogo -NoProfile -File build/test.ps1 -Dotnet .tools/dotnet/dotnet
bash -n worker/nas-worker.sh
```

`bootstrap-mac.py` 目前面向 Apple Silicon Mac；使用固定版本与摘要验证，下载到 `.tools`。测试证据留在 `artifacts/tests/<本次ID>`。需要检查窗口 C# 引用时，先运行 App restore/build 生成 XAML 的 `input.json`（Mac 的 XAML 步骤预期失败），再运行 `python3 build/check-native-csharp.py`；它不会打开或操作窗口。

## Windows 集成验证（尚未执行）

由 Windows 测试者逐项记录系统版本、构建产物摘要、预期/实际和日志。

- [ ] `pwsh -File build/windows.ps1`：锁定依赖还原、XAML 编译、真实 caesium 启动、Inno 编译全部通过。
- [ ] 干净 Windows 11 x64 标准用户：安装无需提权，无预装 .NET/Windows App SDK 时可启动。
- [ ] 开始菜单与卸载入口存在；升级保留配置；卸载移除新版程序和新版右键菜单，保留配置、旧项目、旧菜单和产物。
- [ ] 不选右键菜单时不创建菜单；选择时文件/文件夹/空白处的路径正确，中文/空格/单引号/$/& 不被解释成命令。
- [ ] 独立启动为空；`--path` 自动载入；运行中重复启动拒绝覆盖当前来源；不同目录多选被拒绝。
- [ ] 文件、同目录多图、文件夹、空目录、无支持图片目录、递归、符号链接/重解析点、UNC、映射盘均符合清单与输出规则。
- [ ] 各预设与高级参数调用真实 1.4.0 引擎；JPEG/JPEG 同格式不传转换参数；无损/不缩放互斥正确。
- [ ] 已有 `_compressed`、输出重名、A/a 名称、转换同名均不覆盖；旧结果不计入本次统计；图片实际体积配对正确。
- [ ] 预演无输出目录/压缩产物；损坏图片、写入失败、权限不足、引擎缺失/异常退出保留成功产物并展示失败详情。
- [ ] PC 取消真正停止本任务进程树；快速取消/准备阶段取消/关闭后重开能正确确认任务状态。
- [ ] NAS 设置测试不隐式保存；旧配置导入不修改原文件；非 22 端口、公钥/SSH agent 正常。
- [ ] NAS 共享归属及 Samba 真实路径映射正确，包含特殊字符；SSH 连通但缺少引擎、不可达、错误密钥、共享无权限分别正确提示；允许手动选 PC。
- [ ] NAS Bash 在真实服务器运行：枚举、输出冲突、字节统计、预演、异常图片、取消均通过。
- [ ] NAS 断线不误报停止；远端独立会话继续/结束可重查；PID 重用不误杀；未确认的进程组不会显示已取消；恢复日志保留本次成功/失败计数。

## 用户视觉与交互验收（代理未执行）

- [ ] A 版布局顺序、760 × 640 默认逻辑尺寸、固定任务区与内容滚动。
- [ ] 指定浅/深颜色，跟随系统及手动主题即时保存，重启还原。
- [ ] 100% / 150% / 200% 缩放；窗口尺寸调整与跨屏 DPI；长路径/长错误内容不遮挡主要操作。
- [ ] Tab 顺序、焦点可见、键盘选择预设/引擎/参数、Enter/Escape 对话框操作。
- [ ] 添加/拖入/清空、设置/导入/连接测试、空状态/执行中/成功/部分失败/不可用/取消中/未知状态均可理解。

以上未通过前，不能把当前源码交付表述为“安装包已构建”“Windows 运行已验证”或“视觉已验收”。
