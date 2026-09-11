# 界面与功能问题清单（ImgZip A 版 WinUI）

> 状态：收集中。用户逐条提出；标注「本版修复」的确认后统一修改并验证，标注「后续版本」的进入版本规划。
>
> 记录时间：2026-09-10
>
> 实施进度（0.3.0）：第 1、2 条已完成代码改动；第 5 条已完成 PC 侧组批并验证；第 4、6 条待实现。

# 本版修复

## 1. 标题栏窗口按钮颜色不对

**状态**：已修复（0.3.1，见第 7 条）

**现象**：右上角最小化 / 最大化 / 关闭三个按钮的非悬停状态正常，但悬停与按下时底色和图标颜色与主题不匹配：出现深灰底配白色图标、与浅色主题其余部分不一致的情况；关闭按钮的红色保持系统默认。

**定位**：`src/ImgZip.App/MainWindow.xaml.cs` 的 `ApplyCaptionTheme()` 只设置了 4 个属性——`ButtonBackgroundColor`、`ButtonInactiveBackgroundColor`、`ButtonForegroundColor`、`ButtonHoverBackgroundColor`，未设置悬停前景色、按下底色/前景色与非活动前景色，这些状态由系统默认值接管。

**修复方向**：补全标题栏颜色属性（悬停前景、按下底色与前景、非活动前景），统一按应用调色板取色；深色主题用 `#333` 系悬停底色，浅色主题用 `#E9E9E9` 系；随 `ActualThemeChanged` 重新应用；关闭按钮保留系统红色悬停。

## 2. 执行位置由下拉框改为两个按钮二选一

**状态**：已完成代码改动（待装机验证）

**现象**：主界面「执行位置」一栏当前用 `ComboBox` 下拉选择引擎（本机 PC / NAS），交互偏隐蔽，期望改为两个并排按钮，点击即二选一。

**定位**：`src/ImgZip.App/MainWindow.xaml` 第 38 行使用 `ComboBox`，绑定 `SelectedIndex="{Binding EngineIndex, Mode=TwoWay}"` 并在 `SelectionChanged` 调用 `Engine_Changed` → `ViewModel.ProbeSelectedAsync()`；相关状态在 `MainViewModel`（`EngineIndex`、`NasEligible`、`EngineStatus`、`CanStart`）。

**修复方向**：用二选一控件替换下拉框，优先方案是 WinUI `RadioButtons`（`MaxColumns="2"`，天然单选、键盘可达），保留 `SelectedIndex` 双向绑定与 `Engine_Changed` 处理器；NAS 项沿用 `IsEnabled="{Binding NasEligible}"`，整体沿用 `IsEnabled="{Binding CanEdit}"`。若视觉上要与下方预设按钮一致，则改用两个共用分段样式的 `ToggleButton`，点击写回 `ViewModel.EngineIndex` 后再触发探测。两种方案都需保证：未配置 NAS 时 NAS 项置灰、切换后仍刷新 `EngineStatus`、`CanStart` 判定不变。

## 3. 待补充

**状态**：等待用户描述

## 7. 标题栏右侧出现白块、按钮不可见（第 1 条的真正原因）

**状态**：已修复（0.3.1）

**现象**：应用内切换/跟随为深色主题、而 Windows 系统主题为浅色时，标题栏右侧出现一块白色矩形，最小化/最大化/关闭按钮几乎不可见（白底白图标）。

**定位**：`MainWindow.xaml.cs` 的 `ApplyCaptionTheme()` 只设置了 `AppWindow.TitleBar.Button*` 系列颜色，从未设置**标题栏本身**的 `BackgroundColor` / `InactiveBackgroundColor` / `ForegroundColor` / `InactiveForegroundColor`。而窗口使用 `ExtendsContentIntoTitleBar = false`，标题栏区域由系统按**系统主题**绘制（浅色 → 白底），按钮图标颜色却按应用内 `Root.ActualTheme`（深色 → 白图标）计算，于是出现白底白字；系统与应用主题相反时必然复现。

**修复方向**：在 `ApplyCaptionTheme()` 中同时按应用主题设置标题栏背景与前景——深色用 `#1F1F1F` 背景 / 白色前景，浅色用 `#F3F3F3` 背景 / `#1F1F1F` 前景，并设置非活动态；按钮仍保持 `Transparent` 底色以显示同一背景。需覆盖“系统浅色 + 应用深色”“系统深色 + 应用浅色”两种反向组合，并在 `ApplyTheme()`、`ActualThemeChanged` 与窗口激活时重新应用（`Root.ActualTheme` 的更新时机可能晚于主题切换）。可选的长效方案是改为 `ExtendsContentIntoTitleBar = true` + `SetTitleBar` 自绘标题区，但会牵动布局，先做配色修复。

**已实现（0.3.1）**：`ApplyCaptionTheme()` 现在同时设置标题栏 `BackgroundColor / InactiveBackgroundColor / ForegroundColor / InactiveForegroundColor`（深色 `#1F1F1F` + 白前景、浅色 `#F3F3F3` + `#1F1F1F` 前景），按钮保持透明底，原有的悬停/按下配色不变。需目视确认两种反向主题组合下的观感。

**0.3.1 后仍未修好（0.3.2 修正）**：用户实测深色下——未悬停且聚焦时仍是白底白字（按钮不可见）、悬停正常（深灰底白符号）、未聚焦时浅底淡灰符号。说明**标准标题栏并不接受 `AppWindow.TitleBar.BackgroundColor / InactiveBackgroundColor`**（这组属性只在 `ExtendsContentIntoTitleBar = true` 的自绘标题栏下生效），背景仍由系统按系统主题绘制，而按钮前景却按应用主题取白色，于是出现白底白字；只有按钮类的悬停/按下属性生效。**0.3.2 修复**：改用 DWM 的 `DWMWA_CAPTION_COLOR(35)` / `DWMWA_TEXT_COLOR(36)` 直接给系统绘制的标题栏着色（深色 `#1F1F1F` + 白文字，浅色 `#F3F3F3` + 深文字），随主题切换重新应用；仍保留原按钮配色。需用户复核深色/浅色、聚焦/未聚焦、悬停三态。

## 8. 非 100% 缩放下默认窗口偏小（DPI 计算时机错误）

**状态**：已修复（0.3.1）

**现象**：在 150% 显示缩放的机器上，应用启动后窗口明显偏小（高约 427 逻辑像素），任务列表一出现就非常挤，用户需要每次手动把窗口拉大。

**定位**：`MainWindow.xaml.cs` 构造函数里在 `Activate()` 之前就调用 `GetDpiForWindow(...)` 计算 `scale`，此时窗口尚未与显示器关联，返回 96（scale=1.0），于是 `AppWindow.Resize(760 × scale, 640 × scale)` 实际按 **760×640 物理像素**创建窗口；在 150% 缩放下只相当于约 507×427 逻辑像素。实测佐证：默认启动窗口为 760×640 物理像素，而当前显示 DPI 为 144（150%）。

**修复方向**：把尺寸计算推迟到窗口已关联显示器之后——在 `Root_Loaded`（或 `Activate()` 之后）用 `Root.XamlRoot.RasterizationScale` 取得真实缩放，再按“逻辑尺寸 × 缩放”调用 `AppWindow.Resize`；同时评估把默认逻辑尺寸从 760×640 提高到能容纳任务列表的值（约 860×760），并可选实现“记住上次窗口大小/位置”（写入 `%LOCALAPPDATA%\ImgZip` 配置，启动时恢复，超出工作区时回退到默认并居中）。

**补充（双屏实测，已用 Windows 屏幕设置核对）**：用户环境两块显示器**均为 150% 缩放**——

- 主屏（Windows 显示 2）：**3840×2160**，逻辑工作区约 **2560×1400**，位置 (0,0)
- 副屏（Windows 显示 1）：**1440×2560 竖屏**，逻辑工作区约 **960×1667**，位置在左侧

此前脚本里读到的 2560×1440 / 960×1707 是 DPI 虚拟化后的逻辑值（脚本进程 DPI 不感知），不是物理分辨率；ImgZip 窗口实测 DPI 144，与 150% 缩放一致。因此**不能写死物理尺寸**：同一尺寸在 4K 主屏与 960 逻辑宽的竖屏上表现完全不同，竖屏按 150% 换算会超出屏宽。修复必须“按窗口所在显示器”计算：取该显示器的缩放与工作区，尺寸 = `min(逻辑默认值, 工作区逻辑尺寸 - 边距)` × 缩放，并夹取到工作区内再居中；同时用 `DisplayArea.GetFromWindowId(AppWindow.Id, Fallback.Nearest)` 而不是固定 `Primary`，避免窗口被强制放到主屏。记忆位置时也要用 `DisplayArea.GetFromPoint` 校验保存的矩形仍落在某个显示器可见区域内，否则回退默认并居中。

**已实现（0.3.1）**：尺寸计算从构造函数移到窗口加载后（`Root_Loaded`）执行，使用 `Root.XamlRoot.RasterizationScale` 与窗口所在显示器的 `WorkArea`；默认逻辑尺寸提升为 **860×740**，并夹取到工作区（保留 40px 边距，最小 480×360）；新增窗口状态记忆——关闭时把物理矩形写入 `%LOCALAPPDATA%\ImgZip\window-state.json`，启动时若该矩形仍落在某个显示器可见区域（`DisplayArea.GetFromPoint`）则恢复并夹取，否则回退默认并居中。用户选择的位置策略为**跟随上次位置**。

**验证结果**：首启窗口为 860×740 逻辑（150% → 1290×1110 物理，居中）；关窗后状态文件写入 `{"X":1275,"Y":555,"W":1290,"H":1110}`；重开后恢复到同一位置与尺寸。

## 9. 已完成的任务残留为“状态待确认”

**状态**：已修复（0.3.1）

**现象**：完成一次任务后重新打开窗口，上一次**已成功**的任务仍留在列表里并显示“状态待确认”，需要点“重新检查”才能消掉；与约定“成功任务自动移除、只保留失败/取消/待确认”不符。

**现场证据**：`%LOCALAPPDATA%\ImgZip\tasks\f83da2dcccac4116847f2341bf56a57f.json` 残留（engine=pc、operation=run，来源为 `\\WHY-FN\Other\...\清水凪 - 后辈的制服4`），而 `active-job.json` 已被正确删除；`jobs\` 下有 9 个任务目录。

**定位（清理时机竞态）**：`MainViewModel.RunTaskAsync` 在 `await worker.ExecuteAsync(...)` 返回后立即检查 `task.TerminalConfirmed`，但 worker 事件是通过 `dispatch(() => { task.Apply(e); Mirror(task); })` 投递到 UI 线程的——`ExecuteAsync` 返回时终态事件可能**还没被派发执行**，于是 `TerminalConfirmed` 仍为 false，`tasks/<jobId>.json` 就不会被删除、任务也不会从列表移除。稍后事件派发执行时 `Mirror()` 删掉了 `active-job.json`，但已经没有任何代码去清理 `tasks/<jobId>.json`，导致下次启动时 `RecoverTasksAsync` 把它恢复成 `unknown` 任务。

**修复方向**：把“终态收尾”从 `RunTaskAsync` 的 await 之后移到**终态事件被应用之后**执行——在 `dispatch(() => { task.Apply(e); Mirror(task); })` 内判断 `task.TerminalConfirmed` 并调用幂等的 `FinalizeTaskAsync(task)`（删除 `tasks/<id>.json`、成功则从 `Tasks` 移除、刷新 `TaskSummary`）；`RunTaskAsync`/`CancelTaskAsync`/`InspectTaskAsync` 尾部保留一次幂等调用兜底。给任务加 `Finalized` 标志避免重复收尾。保留策略维持：`failed/partial/cancelled/unknown` 保留，`succeeded/dryRun/emptyResult` 自动移除。

**附带处理**：用户机器上那条残留记录可手动删除（或等修复版启动后由代码清理）；修复时需要同时处理“启动恢复时若任务记录对应 worker 已是终态则自动收尾”的情况，避免旧记录长期堆积。

**已实现（0.3.1）**：新增幂等的 `FinalizeTaskAsync(task)`（`Finalized` 标志防重复），并在 `task.Apply(e)` 之后、终态确认时立即执行收尾——删除 `tasks/<id>.json`；`succeeded / dryRun / emptyResult` 从任务列表移除，`failed / partial / cancelled / unknown` 保留；`RunTaskAsync / CancelTaskAsync / InspectTaskAsync` 尾部保留幂等兜底调用。新增回归用例“成功任务收尾后列表为空且任务记录被删除”，测试总数 15 项全部通过；用户机器上那条残留记录已清除。

# 后续版本

## 4. 本机 PC 支持多任务并行，NAS 维持单任务

**状态**：已实现（待装机验证）

**需求**：当前全局只允许一个任务进行；下个版本让**本机 PC 引擎**支持多任务并行执行，**NAS 引擎仍只支持单任务**。

**现状与改动影响面**：

- `MainViewModel` 目前以单任务状态机实现：单一 `State`、`IsLocked = IsRunning || State == "unknown"`、`CanEdit = !IsLocked`，任务未结束时 `SetSourcesAsync` 直接拒绝新来源（提示“当前任务尚未结束，本次来源未载入。”）。
- 任务状态固定写在单一 `active-job.json`（`store.DirectoryPath` 下），Worker 请求只携带一个 `JobId` 与一个 `StateDirectory`；probe/cancel 均针对该唯一任务。
- 应用按用户单实例，`InstanceBroker` 把新 `--path` 转交现有窗口；多任务后应改为入队新任务而非拒绝。
- 「执行位置」当前是全局选择，多任务并行后需要变成**每任务属性**（不同任务可各用 PC/NAS）。

**实现要点（待细化）**：

1. 任务模型从单任务改为任务集合：每个任务独立 `JobId`、状态目录、来源、引擎、进度与终态；状态目录按任务拆分（如 `jobs/<jobId>/`），并保留对旧 `active-job.json` 的迁移或兼容读取。
2. 并发策略：PC 任务并行（建议并发上限 `min(4, CPU 核数)`，可配置）；NAS 任务保持全局互斥，任意时刻至多一个 NAS 任务。
3. 输出目录分配需原子化：`_compressed` / `_compressed_2`… 的编号在并行任务间不得冲突。
4. 界面：任务列表（每项含进度、取消、打开输出、失败详情），主窗口布局随之调整。
5. 协议层：Worker 事件按 `JobId` 复用现有流式协议，客户端需支持多路复用与按任务 probe/cancel。

**待确认的开放问题**：PC 并发上限取多少、是否需要用户可调；NAS 任务运行期间是否允许同时启动 PC 任务；预演任务是否占用并发额度。

**已实现（0.3.0）**：新增 `CompressionTaskViewModel` 任务模型与 `MainViewModel.Tasks` 任务集合；PC 任务按 `AppConfig.PcConcurrency`（0=自动=CPU 核数，设置界面可调 1–64）并发，NAS 任务彼此互斥但可与 PC 任务并行；每任务启动时按 `threads = max(1, 核数 / 并行任务数)` 分配引擎线程；任务记录写入 `tasks/<jobId>.json`，启动时恢复未确认任务，旧 `active-job.json` 迁移为待检查任务；主界面底部改为任务列表（逐任务进度、当前文件、取消/重新检查/打开输出/失败详情），成功的任务自动移出列表，失败/取消/待确认保留；草稿区域在任务运行期间保持可编辑，可继续排队新任务；重复来源会聚焦已有任务而不重复入队。测试新增 3 项（含 PC 并发上限、NAS 串行），现共 14 项全部通过。

## 5. PC 引擎加速：消除逐文件进程开销

**状态**：PC 侧已完成并验证（NAS 侧见第 6 条）

**现象**：PC 模式下每张图片都要新起一次引擎进程，且文件之间完全串行，批量任务的大部分时间花在固定开销上而不是编码上。

**实测证据**（本机 20 张 1.7 MB JPEG、q90）：

| 方式 | 总耗时 | 每张平均 |
|---|---|---|
| 逐文件调用 20 次（当前实现） | 30.27 秒 | ≈1.5 秒 |
| 单次批量调用 20 个文件 | 2.39 秒 | ≈0.12 秒 |

差距约 12.7 倍，即每张图约 1.4 秒的纯进程启动开销（Windows 安全软件扫描 6 MB 引擎可执行文件是主要嫌疑）。

**定位**：

- `worker/imgzip-worker.ps1` 的 `Run-Local` 按 `for ($index=0; $index -lt $total; $index++)` 逐文件 `Start-Child $request.enginePath`，一文件一进程。
- `worker/nas-worker.sh` 结构相同（每文件调用一次引擎）；Linux 进程启动便宜得多，且 N5105 本身编码慢，故 NAS 侧优先级较低。
- `--threads` 默认 4、上限 64，只并行单张图的编码内部，不解决文件级串行。
- 每文件还有额外固定开销：创建 staging 目录、扫描产物、移动到目标、`WaitForExit(100)` 轮询。

**加速方案（按收益排序）**：

1. **组批调用**：按“是否需要 `--format` 转换”分组，把同参数文件一次传给 caesiumclt（引擎原生支持多输入）；实测可拿到 10 倍以上收益。需保留现有唯一命名映射与部分失败统计。
2. **文件级并行池**：PC 侧同时运行 N 个编码任务（与第 4 条的 PC 多任务并行共用调度器）；NAS 保持单任务串行。
3. **削减每文件杂项开销**：改为事件等待而非 100 ms 轮询；用临时文件名写完后原子改名，替代 staging 目录 + 扫描 + 移动。
4. **线程数自适应**：PC 默认按 CPU 核数取 `--threads`（当前固定 4 是按 NAS 4 核定），保留手动覆盖。

**代价与风险**：组批后无法再用“一文件一进程”推导逐张进度，进度需改由引擎输出或输出目录变化推导；取消语义、部分失败统计、大小写冲突处理必须保持一致。建议与第 4 条一起实现，共用一个并发/调度层。

**已实现（PC）**：`worker/imgzip-worker.ps1` 的 `Run-Local` 已改为按“是否需要 `--format` 转换”分组、组内按批调用引擎（批大小 `clamp(2×核心数, 4, 16)`，批内源文件名判重避免 stage 覆盖）；批次运行时轮询 stage 目录，产物写完（可独占打开）即搬运并逐张上报，批结束后按引擎输出补齐失败项。假引擎同步支持多输入与 `--verbose 3` 逐文件输出，现有 12 项检查全部通过。

**PC 验证结果**：真实引擎、20 张 1.7 MB JPEG、q90，经 worker 端到端耗时 **8.8 秒**（旧逐文件实现同规模约 30 秒，直接批量调用为 2.39 秒）；逐张进度真实流式，首个文件事件 2.35 秒、最后一个 8.69 秒。

## 6. NAS 引擎加速：批量调用以吃满多核

**状态**：已实现（待装机验证）

**现象**：NAS 侧同样是一文件一次引擎调用，且 `--threads` 传的是配置值（默认 4）。实测表明这种方式**实际只用了 1 个核**，NAS 的 4 核 CPU 大部分时间是闲置的。

**实测证据**（NAS：N5105 4 核 / 7.7 GB 内存，20 张 JPEG，q90，本地磁盘无网络传输）：

| 调用方式 | 总耗时 | 相对当前 |
|---|---|---|
| 逐文件调用，`--threads 4`（当前实现） | 49.74 秒 | 1.00× |
| 单次批量，`--threads 1` | 49.82 秒 | 1.00× |
| 单次批量，`--threads 2` | 26.09 秒 | 1.91× |
| 单次批量，`--threads 4` | 15.48 秒 | **3.21×** |
| 单次批量，`--threads 0`（引擎自动） | 15.40 秒 | 3.23× |
| 单次批量，`--threads 8` | 15.31 秒 | 3.25×（无进一步收益） |

**关键结论**：当前的“逐文件 + `--threads 4`” ≈ 批量 `--threads 1`，说明 caesiumclt 的 `--threads` 是**作业级并行**而非单张图内部并行——单文件输入时给再多线程也只有一个作业，等于单核运行。改成批量调用后 4 核才真正吃满，收益约 3.2 倍；超过核心数（8 线程）不再增长，说明 CPU 已饱和。

**定位**：`worker/nas-worker.sh` 在 `for ((index=0; index<total; index++))` 中逐文件执行 `"$engine" "${args[@]}" -o "$stage" "$file"`，每文件一个进程、一个 staging 目录、一次 `find` 扫描与一次移动。

**优化方案**：

1. **组批调用（首要）**：按“是否需要 `--format` 转换”分组，同格式批次一次传入多个文件；`--threads` 取 `min(nproc, 批次文件数)` 或直接 `0` 让引擎自动决定。预计收益 3 倍左右。
2. **批次大小**：建议每批 ≤ 2×核心数（4 核 → 8 文件/批），避免内存与磁盘抖动；NAS 仍维持单任务，不做多批并行。
3. **削减每文件开销**：取消每文件的 staging 目录 + `find` + 移动，改为批次临时目录 + 完成后按 manifest 统一改名，同时保持取消（进程组 kill + journal）与部分失败统计语义。
4. **进度与统计适配**：组批后逐张进度需由引擎输出或输出目录变化推导；最终产物仍需映射回计划文件名，保证唯一命名与大小写冲突处理不变。
5. **优先级（可选）**：默认以普通优先级跑满 4 核；如需降低对 SMB/fnOS 服务的影响，可提供“安静模式”（正向 nice + 更小批次）；要提高优先级需 root（如 `sudo nice -n -5`），默认不启用。
6. **验证指标**：记录 files/min 与 CPU 占用，确认 4 核被吃满、温度与并发服务无明显退化；用同一组样本对比优化前后耗时。

**已实现（0.3.0，采用有界并行池）**：`worker/nas-worker.sh` 的逐文件循环改为按 `THREADS` 并发的进程池——每个文件仍在独立 stage 目录里由单个 `--threads 1` 的引擎作业处理、硬链接晋升并逐张上报事件，子进程继承 runner 的 setsid 会话，因此既有取消（杀进程组）与 unknown 判定语义完全不变。父进程统一累计计数并按完成顺序输出 `file` 事件，保证 JSON 行不交错。

**NAS 实测（N5105 4 核 / 7.7GB，20 张 JPEG，q90，本地磁盘）**：

| 并行数 | 总耗时 | 产出 | file 事件 | 终态 |
|---|---|---|---|---|
| `THREADS=1` | 65.5 秒 | 20 | 20 | succeeded |
| `THREADS=4` | 26.2 秒 | 20 | 20 | succeeded |

即 **2.5×** 提速（相对并行数 1），产出与逐张进度均正确。说明：单一批量调用实测更快（15.5 秒，3.2×），但需要重做每文件的唯一命名、硬链接晋升与部分失败统计；当前先落地风险更低的并行池，批量调用作为后续可选优化（会改变进度粒度）。
