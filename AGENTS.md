# ImgZip 接手指南

给第一次进这个仓库的人或 AI。目标是让你在 10 分钟内知道**它是什么、代码怎么分、改哪里、怎么验、哪里有坑**，不必先读完 2000 行 C#。

---

## 1. 这是什么

Windows 图片批量压缩工具。WinUI 3 + C# / .NET 10，双引擎：

- **PC 引擎**：随安装包自带 `caesiumclt.exe`，多进程并发组批调用。
- **NAS 引擎**：经 SSH 在 fnOS / 类 Linux NAS 上执行固定 Bash 脚本。

一个进程、多个窗口，**一窗一任务**：每个窗口承载一个压缩任务（草稿 → 运行 → 结果），窗口不重复使用。PC 任务按 CPU 核数并发，NAS 任务串行（可与 PC 并行），名额由应用级单例统一分配。

---

## 2. 代码怎么分

```
src/ImgZip.Core/        纯 .NET，不引用 WinUI —— 可跨平台编译与测试
src/ImgZip.App/         WinUI 3 外壳：窗口、对话框、单实例、窗口子类化
worker/                 执行层，两个独立脚本，不经由 C# 逻辑
tests/                  控制台检查（没有 xUnit，见下）
build/                  构建与测试脚本
```

**硬约束：`ImgZip.Core` 不得引用 `Microsoft.UI.*` 或任何窗口类型。** 这条线是它能被无 UI 测试的直接原因（[PROTOCOL.md:3](docs/PROTOCOL.md#L3)）。加功能时如果发现需要 Core 知道窗口，那说明该逻辑应该留在 App 层。

### Core 逐文件职责

| 文件 | 职责 | 规模 |
| --- | --- | --- |
| `TaskCoordinator.cs` | **应用级单例**。持有全部任务、PC/NAS 名额池、持久化写入时机、权威配置、崩溃恢复入口。所有窗口共用一个实例 | 589 行 |
| `MainViewModel.cs` | 单窗口的草稿状态与绑定属性（来源、预设、引擎、高级设置、派生可见性） | 394 行 |
| `MainViewModel.Tasks.cs` | `MainViewModel` 的 `partial` 部分：把任务提交给协调者、取消、重查、强制关闭 | 92 行 |
| `CompressionTaskViewModel.cs` | 单个任务卡片的展示状态（进度、排队原因、失败明细、终态） | 170 行 |
| `TaskStore.cs` | 任务记录读写，`%LOCALAPPDATA%\ImgZip\tasks\<jobId>.json`，schema v2 | 131 行 |
| `WorkerClient.cs` | 启动 PowerShell 子进程、写一行 JSON、读 JSON-lines 事件；也实现 `IWorkerTracker` | 150 行 |
| `Models.cs` | 线协议记录（`WorkerRequest` / `WorkerEvent`）、`AppConfig`、`CompressionOptions`、`Display` 文案 | 165 行 |
| `ForceClose.cs` | 关闭窗口时终止执行层进程树 + 清理暂存目录；`StagingCleaner` | 92 行 |
| `JobDirectories.cs` | `jobs/<jobId>/` 的建立与回收，`worker.pid` 就在里面 | 63 行 |
| `RecallAction.cs` | 无来源启动该做什么的**纯函数决策**（开窗口 / 叫草稿 / 全部呼出） | 33 行 |
| `ConfigStore.cs` | `local-config.json` 的读写与原子保存 | 30 行 |
| `SystemProcessProbe.cs` / `IProcessProbe.cs` | 判断 PID 是否仍存活（可替换，测试用 `FixedProbe`） | 37 / 14 行 |
| `IWorkerTracker.cs` | 「执行层起来了」事件，让 PID 被记进记录 | 19 行 |

### App 逐文件职责

| 文件 | 职责 |
| --- | --- |
| `App.xaml.cs` | 单实例判定、建协调者、**窗口创建与召回策略**（`Recall` / `RevealAll` / `RevealSiblings` / `FindBlankDraft`）、级联偏移、未处理异常写日志 |
| `MainWindow.xaml(.cs)` | 单窗口外壳：自绘标题栏、主题、位置记忆、拖放与选择器、所有按钮事件转发到 ViewModel |
| `WindowReveal.cs` | `SetWindowSubclass` 挂 `WM_ACTIVATEAPP`，实现「激活一个窗口时把同组最小化窗口一起还回来」 |
| `InstanceBroker.cs` | 进程间：具名互斥量判首实例 + 具名管道转发来源；发送前调 `AllowSetForegroundWindow` |
| `SettingsDialog.xaml(.cs)` | NAS 连接配置，含 `ReadEngineMode` |
| `Themes/Colors.xaml` | 颜色 token，见 [DESIGN-A.md](docs/DESIGN-A.md) |

### 执行层

| 文件 | 职责 |
| --- | --- |
| `worker/imgzip-worker.ps1` | 无界面入口。ops：`probe / resolve / run / cancel / inspect`。标准输入一行 JSON，标准输出 JSON-lines，标准错误为诊断 |
| `worker/nas-worker.sh` | 固定远程脚本，经 `bash -s` 送入，并发夹取到 `min(请求值, nproc, 4)` 并按可用内存下调 |

协议细节见 [PROTOCOL.md](docs/PROTOCOL.md)。**改 worker 的输出字段必须同步改 `Models.cs` 与 `WorkerClient.cs`**，否则事件会被静默丢弃或抛 `InvalidDataException`。

---

## 3. 怎么建、怎么测

前置：.NET SDK **10.0.400**（`global.json` 锁定）、PowerShell 7、Inno Setup 7.1.0。

```powershell
# 只跑检查（不建安装包）—— 日常改动跑这个
pwsh -NoProfile -File build/test.ps1

# 完整构建 + 安装包
pwsh -NoProfile -File build/windows.ps1 -CompilerPath .tools/inno/ISCC.exe

# 跳过检查，只重建安装包（版本戳需要更新时）
pwsh -NoProfile -File build/windows.ps1 -CompilerPath .tools/inno/ISCC.exe -SkipTests
```

版本号唯一来源是 [Directory.Build.props](Directory.Build.props) 的 `<Version>`。安装包产物为 `artifacts/installer/ImgZip-<版本>-win-x64-setup.exe` 及其 `.sha256`。

**安装包里的版本戳取自构建时的 git 提交**，所以顺序必须是「先提交，再打包」，否则装出来的 `ProductVersion` 指向旧提交——这一条踩过。

测试写法见 [TESTING.md](docs/TESTING.md)。

---

## 4. 坑（都不报错，只给出错误结论）

### 4.1 MSBuild 可能跳过重编译

还原一个被改动过的源文件后，时间戳变旧，`CoreCompile` 可能整步跳过，于是你在测试一个陈旧的 DLL。现象是「修复明明写对了却不生效」，非常容易误判成逻辑错误。

```powershell
(Get-Item <改过的文件>).LastWriteTime = (Get-Date).AddMinutes(2)
```

改完再 build。做变异验证（临时改坏一处看测试是否变红）时尤其必须先做这一步，否则「变异后测试仍绿」会被误读成测试无效。

### 4.2 编辑 XAML / 部分 C# 会丢 BOM

`MainWindow.xaml` 是 UTF-8 **带 BOM** + CRLF。用默认 UTF-8 写回会丢 BOM，diff 里出现整行 `-﻿<Window` → `+<Window` 的假改动。写回时用 `[System.Text.UTF8Encoding]::new($true)`。

各文件现状（改动前先照这个核对）：

| 文件 | BOM | 换行 |
| --- | --- | --- |
| `src/ImgZip.App/MainWindow.xaml` | **有** | CRLF |
| `src/ImgZip.App/MainWindow.xaml.cs` | 无 | CRLF |
| `src/ImgZip.App/SettingsDialog.xaml` | 无 | CRLF |
| `src/ImgZip.App/App.xaml.cs` | 无 | LF |
| `src/ImgZip.Core/MainViewModel.cs` | 无 | LF |
| `tests/ImgZip.Tests/LifecycleTests.cs` | 无 | CRLF |
| `tests/ImgZip.Tests/Program.cs` | 无 | LF |
| `README.md`、`docs/WINDOWS.md`、`docs/VALIDATION.md`、`docs/DESIGN-A.md`、`docs/PROTOCOL.md` | 无 | CRLF |
| `docs/UI-ISSUES-0.5.0-followup.md`、`docs/TESTING.md`、`AGENTS.md` | 无 | LF |

改文档前先跑一次核对，别信这张表：

```powershell
$b = [IO.File]::ReadAllBytes($path)
"BOM={0} CRLF={1}" -f ($b.Length -ge 3 -and $b[0] -eq 0xEF -and $b[1] -eq 0xBB -and $b[2] -eq 0xBF),
                   ([IO.File]::ReadAllText($path).Contains("`r`n"))
```

仓库没有 `.gitattributes`，靠全局 `core.autocrlf=true` 管理换行：文件在库里存 LF，检出时转 CRLF。所以上表描述的是**当前工作区**的样子，新建的文件（`AGENTS.md`、`docs/TESTING.md`）现在是 LF，别人重新检出后会变成 CRLF——这不算问题，只有 BOM 丢了才算。

也别新建 `.gitattributes` 来"统一换行"：它会让全部文件一次性翻转，diff 变成几千行。

### 4.3 `SetForegroundWindow` 会静默失败

在自动化脚本里抢前台，如果没有前台权，它返回 `False` 并且**什么都不发生**。曾因此把一次「点击另一个窗口」的反例测试当成通过，其实那次点击根本没生效。

做窗口类验证时：先把焦点让给别的程序，再用真实点击（`SetCursorPos` + `mouse_event`）而不是 `SetForegroundWindow`，并断言点击确实改变了前台窗口。

### 4.4 Win11 任务栏拿不到 UIA 元素

`MSTaskListWClass` 的 UIA 子元素数为 0，无法用 UI 自动化点任务栏按钮。只能用截图按颜色定位图标，再注入鼠标事件。

### 4.5 一个任务栏按钮点击不产生任何消息

裸点合并后的任务栏按钮只弹缩略图浮层，**不激活任何窗口**，因此不发 `WM_ACTIVATEAPP`。详见 [UI-ISSUES-0.5.0-followup.md 的问题 3](docs/UI-ISSUES-0.5.0-followup.md)。不要再试图用窗口消息修「一次点击还原全部」——没有可挂的触发点。

---

## 5. 改东西之前先读哪份

| 你要改的 | 先读 |
| --- | --- |
| 窗口行为（可见性、关闭、召回） | [FEATURES-one-task-per-window.md](docs/FEATURES-one-task-per-window.md)，特别是「关键约束」与「踩到的坑」 |
| 任务生命周期、名额、持久化 | [FEATURES-one-task-per-window.md](docs/FEATURES-one-task-per-window.md) 阶段 1c–3；[PROTOCOL.md](docs/PROTOCOL.md) |
| 关闭 / 强杀 / 崩溃恢复 | [FEATURES-force-close.md](docs/FEATURES-force-close.md)、[FEATURES-job-directory-reclaim.md](docs/FEATURES-job-directory-reclaim.md) |
| 引擎选择与回落 | [FEATURES-engine-mode-selection.md](docs/FEATURES-engine-mode-selection.md) |
| 界面与配色 | [DESIGN-A.md](docs/DESIGN-A.md) |
| 构建、安装、卸载、诊断路径 | [WINDOWS.md](docs/WINDOWS.md) |
| 某处行为「为什么会这样」 | [UI-ISSUES-*](docs/) 系列，按版本找 |

**这些文档记录的是「当时为什么这么选」，不是现状清单。** 行为以代码为准；发现文档与代码不一致时，改文档，并在提交信息里说明哪一句过期了。

---

## 6. 工作约定

- **改动要能验证。** 每个修复都配一条会因该修复而变红的检查（见 [TESTING.md](docs/TESTING.md) 的变异验证）。
- **不要为了通过检查而放宽断言。** 检查失败优先怀疑实现。
- **提交信息写「为什么」，不写「改了什么」**——后者 `git diff` 已经说明。多行 body 用 `-m` 分段。
- 界面文案与文档均用中文；代码注释也用中文，但类型名、成员名用英文。
- `artifacts/`、`.tools/`、`bin/`、`demo-images/*` 已被 `.gitignore` 排除，不要把产物或演示图提交进来。

---

## 7. 文档地图

| 文档 | 内容 |
| --- | --- |
| [README.md](README.md) | 面向用户：功能、安装、构建、首次配置、用法 |
| [docs/WINDOWS.md](docs/WINDOWS.md) | 面向使用者：构建安装包、安装升级卸载、诊断文件位置 |
| [docs/PROTOCOL.md](docs/PROTOCOL.md) | 执行协议 v1：请求/事件字段、五个 operation 的语义 |
| [docs/DESIGN-A.md](docs/DESIGN-A.md) | A 版界面规范与颜色 token |
| [docs/VALIDATION.md](docs/VALIDATION.md) | 验收清单（含尚未勾选的人工项） |
| [docs/TESTING.md](docs/TESTING.md) | 测试怎么组织、怎么加、替身怎么用、变异验证怎么做 |
| [docs/FEATURES-*.md](docs/) | 各特性的设计与实施记录 |
| [docs/UI-ISSUES-*.md](docs/) | 各版本的用户反馈问题、根因与修复 |
