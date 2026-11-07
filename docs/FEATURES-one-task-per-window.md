# 一窗一任务 + 新建任务前置弹窗

**状态：已记录，未实施。** 目标版本 **0.4.0**。

## 背景

当前所有任务都放在同一个 `MainWindow` 里，用 `ItemsControl ItemsSource="{Binding Tasks}"` 堆叠展示（[MainWindow.xaml:78-111](../src/ImgZip.App/MainWindow.xaml)）。由此产生三个问题：

1. **多任务共用一窗**，任务卡片挤在固定高度 240px 的滚动区里，多个任务同时跑时难以分辨和操作。
2. **第二个任务不会把窗口带到前台**。右键第二个文件夹时，第二个进程经 `InstanceBroker` 把路径转发给第一个进程后退出（[App.xaml.cs:38-41](../src/ImgZip.App/App.xaml.cs)），原窗口只调用了 `Activate()`，实际效果往往只是任务栏闪烁。原因见「附录：Windows 前台锁」。
3. **排队任务没有明确反馈**。NAS 队列串行（`limit` 硬编码为 1，[MainViewModel.Tasks.cs:131](../src/ImgZip.Core/MainViewModel.Tasks.cs)），第二个 NAS 任务会静默停在「排队中」，用户不知道要等多久、在等谁。

## 目标

| # | 要求 | 来源 |
| --- | --- | --- |
| 1 | 每个窗口只承载一个任务 | 用户明确 |
| 2 | 新建任务时直接弹出窗口并置于最前 | 用户明确 |
| 3 | NAS 队列无法立即执行时，点击后提示等待队列 | 用户明确 |

## 窗口模型

窗口生命周期从「应用唯一窗口」改为「任务窗口按需创建」：

```
启动应用（无参数） → 空草稿窗口
右键/拖入来源   → 新建窗口（草稿态，置前）
点击「开始压缩」 → 该窗口转为任务态，不再接受新来源
任务结束       → 窗口停留在结果态（可重复打开输出、查看失败详情）
关闭窗口       → 结束该窗口的任务（见「关闭语义」）
```

### 关键约束：窗口是单次使用的

一个窗口从打开到关闭只跑一个任务。任务结束后**不允许**在同一窗口里再开新任务——否则「一窗一任务」就退化成「一窗一串任务」，又回到多任务共处的老问题。

想再压一批，就再开一个窗口。这条要在 UI 上明确：任务结束后草稿区整体收起，只留结果区和「关闭」按钮。

## 架构拆分（本次改动的主体）

现在的 `MainViewModel` 同时承担了三种职责，必须拆开才能支持多窗口：

| 职责 | 现状位置 | 新归属 |
| --- | --- | --- |
| 草稿状态（来源、预设、引擎、高级参数） | `MainViewModel` | 每窗口一份 |
| 单个任务的进度/状态/失败详情 | `CompressionTaskViewModel` | 每窗口一份（基本可直接复用） |
| **名额分配、任务持久化、配置、NAS 探测** | `MainViewModel` | **应用级单例，所有窗口共享** |

新增应用级协调者（暂命名 `TaskCoordinator`），持有：

```csharp
SemaphoreSlim taskGate;                  // 原 MainViewModel.taskGate
Dictionary<string, CompressionTaskViewModel> taskIndex;  // 跨窗口全部任务
ITaskStore taskStore;
int EffectivePcConcurrency / EffectiveNasConcurrency;
int NasCores; long NasMemoryMb;
```

`WaitForSlotAsync` 里的名额计算（[MainViewModel.Tasks.cs:122-163](../src/ImgZip.Core/MainViewModel.Tasks.cs)）逻辑**完全不变**，只是从「某个窗口的 `taskIndex`」变为「协调者的全局 `taskIndex`」。这是有意的——并发语义不应因为换窗口模型而改变，否则 0.3.5/0.3.6 那批回归测试全部失效。

### 为什么不能简单地「一个窗口一个 MainViewModel」

因为名额必须全局共享。若每个窗口各自持有一个 `MainViewModel`，每个窗口都会认为自己有完整的 PC 并发额度，就会出现 32 核机器上开出 64 个并发任务。协调者必须是进程内单例。

## 待决问题

以下 5 点我给出了推荐，但需要确认后再实施。

### 1. 重启恢复：多窗口怎么恢复？

重启后 `TaskStore.LoadAsync()` 可能返回多条 `queued` / `launching` / `running` 记录。若每个记录开一个窗口，冷启动可能瞬间弹出 5 个窗口，体验很差。

**推荐**：启动时只开一个「待恢复任务」窗口，列出全部未结束记录，每条提供「继续」/「移除」。点击「继续」才为该任务开一个真正的任务窗口。恢复窗口本身不承载任务。

### 2. 无参数启动：复用空白窗口还是新开？

双击 `ImgZip.exe`（没有 `--path`）时，若已有窗口，可能正有个任务在跑。

**推荐**：如果存在**仍在草稿态**（尚未提交任务、无来源）的窗口，就聚焦它；否则新开一个。这样双击不会无限繁殖空窗口。

### 3. NAS 排队提示的形式

用户要求「按钮点击后会提示等待队列」。但在一窗一任务的模型下，点击「开始压缩」后窗口就归属该任务了，提示应该出现在哪里？

**推荐**：不用模态对话框，直接在新窗口的任务卡片上显示排队原因，例如：

> 排队中 · NAS 已有任务在执行，完成后自动开始

理由：模态框要求用户点「确定」才能继续，而排队可能是几分钟的事，用户更可能想先去干别的。同时主界面滑动区已有 `ConcurrencyText` 位可以复用（[MainWindow.xaml](src/ImgZip.App/MainWindow.xaml) 的任务模板里已有该绑定）。

若确定要模态提示，则只在该任务**首次进入排队**时弹一次，不重复弹。

### 4. 关闭语义：关一个窗口还是关全部？

**推荐**：关闭窗口只取消**该窗口的任务**；关闭最后一个窗口时，若仍有其他窗口的任务在跑，提示「还有 N 个任务在运行」。这与现状的 `CancelAllAndWaitAsync`（[MainViewModel.Tasks.cs:272-284](../src/ImgZip.Core/MainViewModel.Tasks.cs)）语义不同，需要新增按窗口取消的路径。

### 5. 窗口位置：多个窗口怎么排列？

`window-state.json` 目前只存单个窗口的 X/Y/W/H（[MainWindow.xaml.cs:108-117](../src/ImgZip.App/MainWindow.xaml.cs)）。多窗口会全部叠在同一位置。

**推荐**：保留一份基准位置/尺寸，新窗口按 `+32, +32` 级联偏移，超出工作区则回绕。关闭时只更新基准位置，不逐个记录。

## 附录：Windows 前台锁（为什么现在不会弹到前面）

这不是简单的 `Activate()` 漏调用——现有一处 `window.Activate()`（[App.xaml.cs:50](../src/ImgZip.App/App.xaml.cs)）是调了的，但无效。

Windows 有前台窗口权限限制：只有当前拥有前台的进程，或通过 `AllowSetForegroundWindow` 被显式授权的进程，才能调用 `SetForegroundWindow` 抢占前台。现状流程是：

```
Explorer（前台） → 启动 ImgZip.exe 实例 B（B 获得前台权）
                 → B 通过管道把路径发给 A 后 Exit()
                 → A 调 Activate()，但 A 不是前台进程 → 被系统拒绝，只闪烁任务栏
```

**两个可选解法**：

| 方案 | 做法 | 代价 |
| --- | --- | --- |
| A. 授权转移 | 实例 B 在 `SendAsync` 前调用 `AllowSetForegroundWindow(A 的 PID)`，允许 A 抢前台 | 需要 A 的 PID 随管道消息传递；B 必须在前台权有效期内完成 |
| B. 进程内自建窗口 | 单实例不变，A 收到路径后创建新 `Window` 并 `Activate()`；新窗口由 A 创建，A 仍非前台 | 仍受前台锁限制，需配合方案 A |

我倾向 **A + B 一起用**：单实例模型不变（多进程各有 mutex 会打架），B 把 PID 或直接调用授权后转发，A 建新窗口并抢前台。注意 `AllowSetForegroundWindow` 需要传入 `ASFW_ANY (-1)` 或目标进程 PID，且调用方自身必须已拥有前台权。

**风险提示**：即使做完这套，Windows 在某些情况（用户正在其他应用输入、UAC 提权、锁屏）仍会拒绝抢前台。这属于系统行为，不应视为 bug。需要在文档里向用户说明「窗口会尽力置前，但系统可能只闪烁任务栏」。

## 改动清单

| 文件 | 改动 |
| --- | --- |
| `src/ImgZip.Core/TaskCoordinator.cs`（新） | 全局名额、`taskIndex`、`taskStore`、NAS 探测；从 `MainViewModel` 抽出 |
| `src/ImgZip.Core/MainViewModel.cs` | 缩为单任务草稿 + 单个 `CompressionTaskViewModel`；删除 `Tasks` 集合相关 |
| `src/ImgZip.Core/MainViewModel.Tasks.cs` | `WaitForSlotAsync` 改用协调者；删除 `CancelAllAndWaitAsync` 或改造为按任务取消 |
| `src/ImgZip.App/App.xaml.cs` | 持有窗口列表；`--path` 到达时新建窗口；授权前台 |
| `src/ImgZip.App/InstanceBroker.cs` | 转发时附带目标 PID，支持前台授权 |
| `src/ImgZip.App/MainWindow.xaml` | 删除任务列表区（`ItemsControl`），改为单任务卡片；任务结束后隐藏草稿区 |
| `src/ImgZip.App/MainWindow.xaml.cs` | 级联窗口位置；关闭只取消本窗口任务 |
| `tests/ImgZip.Tests/*` | 见下 |

## 测试用例

- 名额分配在跨窗口场景下与现状一致：PC 限额、NAS 串行、`olderQueued` 先来先跑
- 两个窗口同时提交 PC 任务，不超过 `PcConcurrency`
- 两个窗口同时提交 NAS 任务，第二个进入排队，第一个完成后再启动
- 单窗口关闭只取消该任务，其他窗口任务不受影响
- 关闭最后一个窗口时，其他窗口仍有任务 → 提示而不是静默退出
- 任务结束后窗口不能再提交新任务
- 无参数启动时复用空白草稿窗口，不重复创建
- 恢复场景：多条未结束记录只开一个恢复窗口，逐个「继续」才开任务窗口
- 级联位置在单显示器与跨屏 DPI 下不越界

## 与其他设计的关系

- **引擎默认选择**（[FEATURES-engine-mode-selection.md](FEATURES-engine-mode-selection.md)）：模式在**新窗口的草稿**创建时生效。`last` 模式记录的是跨窗口的全局值，需存放在应用级配置中，不能挂在窗口实例上。
- **`AppConfig`**：`EngineMode` / `LastEngine` 与本次改动共享同一个全局配置存储，两者应一并实现，避免两次改 `MainViewModel` 结构。

## 风险

这是 0.4.0 里改动面最大的一项。`MainViewModel` 拆分会牵动现有 43 组测试中大量依赖 `vm.Tasks` 的用例——实测统计：`vm.Tasks` 出现 **26 次**，`StartAsync` 10 次，`SetSourcesAsync` 12 次，`CancelAllAndWaitAsync` 与 `Apply` 各 2 次。

建议分 6 个阶段实施，每阶段独立可验证、独立提交。

## 分步实施计划

### 阶段 0：前置准备（不改行为）

**目标**：为拆分铺路，零行为变化。

1. 新建 [`docs/FEATURES-engine-mode-selection.md`](FEATURES-engine-mode-selection.md) 中定义的 `AppConfig` 字段（`EngineMode` / `LastEngine`）。**只加字段与 `Normalize` 兜底，不改 `SetSourcesAsync` 的判定**——默认值 `auto` 保证行为完全不变。
2. 在 `tests/ImgZip.Tests` 补一条"旧配置缺字段 → 等同 auto"的用例。
3. `build/test.ps1` 全绿后再进下一阶段。

**验收**：**44 次执行**全过；`git diff` 只涉及 `Models.cs` 和测试。已完成，见提交 `e67eb5d`。

**风险**：无。这一步可与引擎模式功能并行推进。

### 阶段 1：抽出 `TaskCoordinator`（单窗口行为不变）

**目标**：把应用级职责搬出 `MainViewModel`，但**仍只支持一个窗口**。

这一阶段拆成 1a / 1b / 1c 三步。理由见本节末尾的「为什么不能一步做完」——把「机械搬家」和「多窗口语义」混在同一步，测试失败时无法归因，也就失去了回退信号。

#### 1a：纯机械搬家

| 成员 | 现位置 | 新位置 |
| --- | --- | --- |
| `taskIndex` | [MainViewModel.cs:13](../src/ImgZip.Core/MainViewModel.cs) | `TaskCoordinator` |
| `taskGate` | [:20](../src/ImgZip.Core/MainViewModel.cs) | `TaskCoordinator`（与 `taskIndex` 同宿主，否则互斥失效） |
| `taskStore` | [:19](../src/ImgZip.Core/MainViewModel.cs) | `TaskCoordinator` |
| `UpdateTasksAsync` | [Tasks.cs:6](../src/ImgZip.Core/MainViewModel.Tasks.cs) | `TaskCoordinator` |
| `Record` | [Tasks.cs:23](../src/ImgZip.Core/MainViewModel.Tasks.cs) | `TaskCoordinator` |
| `RecoverTasksAsync` | [Tasks.cs:28](../src/ImgZip.Core/MainViewModel.Tasks.cs) | `TaskCoordinator` |
| `WaitForSlotAsync` | [Tasks.cs:122](../src/ImgZip.Core/MainViewModel.Tasks.cs) | `TaskCoordinator`，**逻辑一行不改** |
| `RunTaskAsync` / `ObserveAsync` / `HandleEventAsync` / `FailBeforeStartAsync` / `FinalizeTaskAsync` | [Tasks.cs:164-242](../src/ImgZip.Core/MainViewModel.Tasks.cs) | `TaskCoordinator` |
| `NasCores` / `NasMemoryMb` | [MainViewModel.cs:48-49](../src/ImgZip.Core/MainViewModel.cs) | `TaskCoordinator`（`WaitForSlotAsync` 直接读，必须与限额同源） |

**必须留在 `MainViewModel` 的**（搬到协调者就是语义错误）：

| 成员 | 理由 |
| --- | --- |
| `primaryRequest`（[:14](../src/ImgZip.Core/MainViewModel.cs)） | 是「本窗口当前镜像哪个任务」的**路由键**。上移后 B 窗口提交会劫持 A 窗口的 `CancelAsync` / `InspectAsync` |
| `Mirror` / `SetPrimary` / `PrimaryTask` | 镜像方法体留在窗口，判据上移到订阅关系 |
| 全部草稿参数、`sources`、`BuildOptions` / `Request` | 快照边界（见下文「隐性耦合 ①」） |
| `EngineStatus` / `EngineIndex` / `PcAvailable` / `NasEligible` | 探测的展示态，每窗口一套；`NasEligible` 还依赖本窗口 `sources` |
| `probeGeneration`、`hasConfigurationError` | 窗口内的交互状态 |

**公开门面必须保留**（否则测试编译失败）：`Tasks`、`HasUnfinishedTasks`、`Apply`、`StartAsync`、`ResumeTaskAsync`、`CancelTaskAsync`、`InspectTaskAsync`、`CancelAllAndWaitAsync`、`CancelAsync` / `InspectAsync`。其中 `Tasks` 改成一行读穿即可让全部 `vm.Tasks` 原样编译：

```csharp
public ObservableCollection<CompressionTaskViewModel> Tasks => coordinator.Tasks;
```

**1a 验收**：**44 次执行、零测试修改**全部通过。

#### 1b：补齐共享所需的机制（仍不引入单例）

1a 只证明「搬家没搬坏」。这一步补多窗口真正需要的六件事，全部**不改变现有 44 次执行的行为**：

1. `EnsureRecoveredAsync()` 单飞（`Task` 缓存在字段里 + gate 保护），替代每窗口各跑一次 `RecoverTasksAsync`。
2. `Attach(sink, dispatch, tasksChanged, report)` 返回 `IDisposable`；`MainWindow.Closed` 里 `Detach()`（现有 [App.xaml.cs:53](../src/ImgZip.App/App.xaml.cs) 只 `broker.Dispose()`，多窗口下这行本身也是 bug）。
3. 任务变更按 `JobId` 路由，而非广播。通知风暴见下文「隐性耦合 ④」。
4. `TaskStore` 加内部 gate。**现状没有任何互斥**（`ConfigStore` 有 `SemaphoreSlim`，`TaskStore` 没有），共享后并发 `SaveAsync` / `DeleteAsync` 会撞同一路径。
5. `UpdateConfigAsync(Func<AppConfig, AppConfig>)` 读-改-写，替代「各窗口持有 `Config` 副本、整份覆盖写盘」。
6. `Config` 上移为协调者的权威副本，`MainViewModel.Config` 改为读穿。理由见下文「隐性耦合 ②」。

**1b 验收**：44 次仍全过；新增 3 组「两个 `MainViewModel` 共享协调者」用例（PC 限额 / NAS 串行 / 关一窗不影响另一窗）。

#### 1c：App 层真正持有单例

`App.xaml.cs` 创建唯一 `TaskCoordinator`，`MainWindow` 构造注入。**既有 44 次不改。**

#### 为什么不能一步做完

阶段 1 的价值是证明「**搬家是纯机械的**」。1a 能干净地证明这一点。

如果同时引入「多窗口共享协调者」，测试失败时会有两种可能原因：机械搬家出错，或产品语义变化。**无法归因**，回退重做就失去了意义。

实测统计（用于判断哪一步动了什么）：

- `new MainViewModel(` 全仓 **17 处**：`Program.cs` 8 处、`LifecycleTests.cs` 8 处、`MainWindow.xaml.cs:29` 1 处。
- 测试执行数 **44 次**：`Program.cs` 29 组 + `LifecycleTests.cs` 14 组（[LifecycleTests.cs:49](../tests/ImgZip.Tests/LifecycleTests.cs) 的 `foreach` 展开出 2 组，故 15 次执行）。
- 其中 **12 次**对以下五点有硬依赖，是 1a 的真正验收线：

| # | 约束 | 违反后果 |
| --- | --- | --- |
| C1 | 构造函数签名逐字不变，协调者参数只能**追加在末尾** | `LifecycleTests.cs:19` 是位置参数调用 `(runtimeWorker, config, "fixture", dispatch, disk)`，中间插参会静默错绑 `FaultStore` |
| C2 | `dispatch` 与 `taskStore` 必须原样透传给协调者 | 漏 `taskStore` → `LifecycleTests.cs:43` 的 `await disk.LaunchEntered.Task` **永久挂起**；漏 `dispatch` → `:202` 的幂等收尾断言失效 |
| C3 | 镜像写入与 `Tasks.Remove` 在 `await` 返回前同步完成 | `Program.cs:286`、`LifecycleTests.cs:36,47,68,202` 共 5 处「await 后立刻断言列表为空」变成偶发失败 |
| C4 | 默认路径下协调者必须是**实例级**，不能是 `static` | `LifecycleTests.cs:75,213` 在同一目录新建第二个 VM 并要求任务列表独立；共享后会重复登记 |
| C5 | `Tasks` / `HasUnfinishedTasks` / `Effective*` / `CanEdit` 必须是读穿投影，不能是快照副本 | UI 显示的限额与 `WaitForSlotAsync` 实际使用的限额可能不是同一个数 |

#### 隐性耦合（1a 必须逐条处理）

以下是子代理分析确认的、**不是简单字段搬移**的地方。

**① 快照边界在哪一刻——`SubmitAsync` 必须接收已构造好的 `WorkerRequest`**

[`Request()`](../src/ImgZip.Core/MainViewModel.cs) 在提交时把 `sources.ToArray()` 与 `Config with { NasHosts = ... }` 拷贝进 `WorkerRequest`。[LifecycleTests.cs:148-161](../tests/ImgZip.Tests/LifecycleTests.cs) 随后改预设、改引擎、改 `Config`，再断言 captured 未受影响。

若 `SubmitAsync` 接收草稿引用或「稍后自己去读」，快照点后移，这个用例必然失败。

**② `Config` 归属是原计划的自相矛盾**

原计划把 `EffectivePcConcurrency` / `EffectiveNasConcurrency` 划给协调者，却把 `Config` 留在窗口。但这两个属性 100% 由 `Config` 派生：

- `EffectivePcConcurrency => Config.PcConcurrency ...`（[MainViewModel.cs:92](../src/ImgZip.Core/MainViewModel.cs)）
- `EffectiveNasConcurrency => Math.Clamp(Config.NasConcurrency, 1, hardwareLimit)`（[:98](../src/ImgZip.Core/MainViewModel.cs)）

而 `WaitForSlotAsync` 直接读这两者（[Tasks.cs:131,139](../src/ImgZip.Core/MainViewModel.Tasks.cs)）。**必须单一真相源**，否则会出现「UI 显示对、调度用错数」的假通过。

**③ `dispatch` 没有新归属——最致命的一处**

现状 `UpdateTasksAsync` 在构造时注入的 `dispatch` 上跑，保证所有生命周期转换都在 UI 线程、在 gate 内执行。抽走后：

- 协调者是应用级单例，**不该持有任何窗口的 `DispatcherQueue`**。窗口关闭后 `TryEnqueue` 返回 `false` 且回调永不执行 → [Tasks.cs:16](../src/ImgZip.Core/MainViewModel.Tasks.cs) 的 `completion.TrySetResult()` 永不调用 → 所有 `await UpdateTasksAsync(...)` **永久挂起**。今天 `TryEnqueue` 的返回值被完全忽略，`WaitAsync()` 也没有 CancellationToken。
- 但这在现有 44 次执行里**不会暴露**（全是单窗口、UI 线程不退出）。

建议：`Action<Action>` 改为 `Func<Action, bool> tryDispatch`，失败时内联执行或让 TCS 带异常完成；签名改成每次调用传入目标窗口的 dispatch。

**④ 通知风暴**

`Mirror` 每个 worker 事件触发一次（每个文件一条 `file` 事件），每次 `NotifyDerived()` 发 **29 个**属性通知（[MainViewModel.cs:147](../src/ImgZip.Core/MainViewModel.cs)）。全局广播 N 窗口 = N×29/文件。必须按 `JobId` 收窄。

另一面：[MainWindow.xaml.cs:37-41](../src/ImgZip.App/MainWindow.xaml.cs) 的 `PropertyChanged` 订阅者会直接改 XAML（`ApplyTheme` / `SyncEngineButtons`），跨线程通知会抛 `RPC_E_WRONG_THREAD`。**协调者在后台线程广播、各 VM 直接转 `NotifyDerived` 是错的**，必须先回到各自 dispatcher。

**⑤ `RecoverTasksAsync` 不是「被调用多次」那么简单**

N 个窗口 = N 次 `TaskStore.LoadAsync()`。四个后果按严重度排：

1. **同一 `JobId` 复活出两个实例**（[Tasks.cs:47](../src/ImgZip.Core/MainViewModel.Tasks.cs) 索引被覆盖、列表多一张卡片），两个实例都会 `Apply` 同一批事件 → `FinalizeTaskAsync` 跑两次 → `LifecycleTests.cs:202` 的 `disk.Deletes == 1` 失败。
2. **把「正在运行」的任务当成「待恢复」**。磁盘上 `queued → launching → running`，后开的窗口会读成 `awaitingResume` 并给出「继续」按钮 → **同一 JobId 可被二次派发**，真正的双执行。
3. **`TaskStore` 无互斥**，并发 `File.Move(temp, path, true)` 与并发删除可能与 `launching` 写入交错，把 `finished` 记录「复活」成 `queued`。
4. `recovery.Errors` 只落到第一个窗口的 `Message`，其他窗口看不到损坏记录提示。

**⑥ `taskGate` 的线程语义会变，但互斥性不变**

`SemaphoreSlim(1,1)` 不绑线程，跨窗口仍正确串行。但「在哪个线程上执行」会变：gate 内的 `Tasks.Insert(0, task)` / `Tasks.Remove` 是 `ObservableCollection` 变更，绑定到 `ItemsControl` 时跨线程会抛异常。另外 `WaitForSlotAsync` 的 `Task.Delay(150)` 轮询叠加后，启动延迟从「≤150ms」变成「≤150ms + 队列深度」。

**⑦ 其他容易漏的**

- `TryFocusDuplicate`（[Tasks.cs:96](../src/ImgZip.Core/MainViewModel.Tasks.cs)）的作用域会从「窗口内去重」变成「全局去重」。对「一窗一任务」模型这**可能是错误的产品行为**——用户可能就想两个窗口压同一个文件夹。
- `probeGeneration` 只保护单窗口内的过期探测。多窗口时 A 的旧探测结果可覆盖协调者的 `NasCores`，B 的名额计算随之抖动。
- `tasksReady → CanEdit` 有**时序漏洞**：共享后新窗口在自己 `Config` 还没加载时 `CanEdit` 就为 `true`，用户可立即点开始，`BuildOptions` 读到的是默认 `AppConfig`。建议拆成协调者的 `RecoveryCompleted` 与 VM 的本地 `initialized` 两个标志。
- `CancelAllAndWaitAsync` 里「**先取消所有未派发的等待者，再取消运行中的任务**」这个顺序（[Tasks.cs:279](../src/ImgZip.Core/MainViewModel.Tasks.cs)）是防「取消一个 → 释放名额 → 等待者趁机启动」的关键，搬移时不能动。
- 协调者必须留在 `ImgZip.Core` 程序集内，否则 `QueueCancellation` / `Execution` / `Request` 的 `internal` 全部要变 `public`，等于把内部状态暴露成 API。

#### 原计划的错误（已修正）

| # | 错误 | 修正 |
| --- | --- | --- |
| E1 | 改动清单写「删除 `CancelAllAndWaitAsync` 或改造为按任务取消」 | 与本阶段「测试不改」的验收**直接冲突**。`LifecycleTests.cs:167,176` 两处调用它，`MainWindow.xaml.cs:283` 的关窗流程也依赖全局语义。**阶段 1 必须原样保留**，按任务取消推迟到阶段 2 |
| E2 | 「24 处 `new MainViewModel(...)`」 | 实为 **17 处**（16 处测试 + 1 处 App） |
| E3 | 「43 组测试」 | 阶段 0 之后是 **44 次执行** |
| E4 | 未提 `dispatch` 的新归属 | 见隐性耦合 ③ |
| E5 | `Config` 归属自相矛盾 | 见隐性耦合 ②，1b 上移为单一真相源 |
| E6 | 未提 `Mirror` / `primaryRequest` 的替代方案 | 见「必须留在 MainViewModel 的」表 |
| E7 | 把 `RecoverTasksAsync` 只当成「多调用几次」 | 见隐性耦合 ⑤ |


### 阶段 2：`MainViewModel` 改为单任务模型

**目标**：窗口不再持有任务集合。

1. `MainViewModel.Tasks`（`ObservableCollection`）替换为单个 `CurrentTask` 属性，类型仍是 `CompressionTaskViewModel`。
2. `TaskSummary`（[MainViewModel.cs:110](../src/ImgZip.Core/MainViewModel.cs)）从"3 个进行中 · 2 个排队 · 共 5 个任务"改为单任务描述，或直接删除。
3. `HasUnfinishedTasks` 改为查询协调者的全局任务，语义变为"应用内是否还有未结束任务"——这与关闭最后一个窗口的判定相关（待决问题 4）。
4. `Failures`（[:55](../src/ImgZip.Core/MainViewModel.cs)）保留但只反映当前任务。

**测试迁移**：这一步开始要动测试。26 处 `vm.Tasks` 映射：

| 原写法 | 新写法 |
| --- | --- |
| `vm.Tasks.Single()` | `vm.CurrentTask` |
| `vm.Tasks.Any(t => t.IsQueued)` | `vm.CurrentTask?.IsQueued == true` |
| `vm.Tasks.Count == 0` | `vm.CurrentTask is null` |
| 需要多任务的用例 | 直接构造两个 `MainViewModel`，共享同一个 `TaskCoordinator` |

多任务相关的用例（"PC 并发上限"、"NAS 串行"、"取消全部"）改为**显式创建多个 `MainViewModel` 实例共享协调者**——这正好是阶段 3 多窗口场景的预演，测试代码可原样保留到阶段 3。

**验收**：43 组全过；新增 2 组"两个 `MainViewModel` 共享协调者"的用例。

### 阶段 3：多窗口外壳

**目标**：App 层支持多窗口，UI 一窗一任务。

1. `App.xaml.cs` 的 `private MainWindow? window` 改为 `List<MainWindow> windows` + 单例 `TaskCoordinator`。
2. `--path` 到达时新建窗口而非复用（[App.xaml.cs:48-53](../src/ImgZip.App/App.xaml.cs)）。无参数启动复用空白草稿窗口（待决问题 2）。
3. `MainWindow.xaml` 删除 `ItemsControl ItemsSource="{Binding Tasks}"` 整块（[MainWindow.xaml:78-111](../src/ImgZip.App/MainWindow.xaml)），换成单任务卡片。
4. 任务结束后草稿区收起（见「关键约束：窗口是单次使用的」）。
5. 窗口关闭只取消本窗口任务；关最后一个窗口时检查协调者全局任务（待决问题 4）。
6. 级联窗口位置（待决问题 5）。

**验收**：手工验证——同时开 3 个窗口各跑一个任务，PC 并发符合设置；关中间那个，另两个不受影响。

### 阶段 4：排队提示

1. 任务卡片增加排队原因显示（复用 `ConcurrencyText` 位）。
2. NAS 队列占满时显示"排队中 · NAS 已有任务在执行，完成后自动开始"。
3. 待决问题 3 定了形式后再定 UI 细节。

**验收**：两个 NAS 任务窗口，第二个显示排队原因；第一个完成后第二个自动开始且提示消失。

### 阶段 5：前台授权

1. `InstanceBroker.SendAsync` 附带目标 PID（[InstanceBroker.cs:18-24](../src/ImgZip.App/InstanceBroker.cs)）。
2. 发送方在 `SendAsync` 前调 `AllowSetForegroundWindow(targetPid)`。
3. 接收方建窗口后调 `Activate()`。

**验收**：从资源管理器右键第二个文件夹，新窗口确实出现在最前（非仅任务栏闪烁）。在用户正在其他程序输入时验证降级行为符合附录说明。

**注意**：这一步最容易受系统环境影响，失败不应阻塞前 5 个阶段。

### 阶段 6：引擎模式功能收尾

阶段 0 只加了字段。这里补完 [`FEATURES-engine-mode-selection.md`](FEATURES-engine-mode-selection.md) 的完整逻辑：

1. 抽出纯函数 `PickEngine(模式, 来源, 资格)`。
2. `SetSourcesAsync` 改用该函数。
3. 设置对话框三选 ComboBox。
4. `EngineButton_Click` 记录 `LastEngine`。
5. 回落提示写入 `EngineStatus`。

**验收**：该文档列出的 9 条测试用例全部通过。

## 阶段依赖关系

```
阶段 0 ──→ 1a ──→ 1b ──→ 1c ──→ 阶段 2 ──→ 阶段 3 ──→ 阶段 4
                                    │
                                    └──→ 阶段 6（必须在阶段 2 之后，因为 last 模式依赖单任务草稿）
                                                  ↑
阶段 5（独立，可与 3/4/6 并行）
```

阶段 0 与阶段 5 相对独立。**1a 是分水岭**——它必须做到「44 次执行、零测试修改」才算成功，这是证明「搬家是纯机械的」的唯一机会。1b 补共享机制，1c 才真正上单例。

## 进度记录

| 阶段 | 状态 | 提交 |
| --- | --- | --- |
| 阶段 0 | ✅ 已完成，44 组通过 | `e67eb5d` |
| 1a | 待开始 | — |
| 1b / 1c / 2-6 | 待开始 | — |


## 不要一次性重写

