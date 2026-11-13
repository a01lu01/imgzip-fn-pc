# ImgZip 0.5.0 后续问题记录

记录日期：2026-11-12（问题 1 与附带发现于 2026-11-13 修复）
状态：**问题 1 已修复**，附带发现已修复。方案见文末「实施记录」。

来源：用户实机使用 0.5.0 安装版时提交的截图与描述。

---

## 问题 1：任务开始后上方界面全部消失

### 现象

点击「开始压缩」后，窗口上半部分（标题区、消息栏、来源卡片、复制选项、压缩预设、执行位置、高级设置、输出路径）**整体消失**，只剩一大片空白，底部保留任务卡片与摘要行。

用户描述：「开始任务后，上方信息全部消失，建议修改为维持显示信息直到关闭窗口。」

任务进行中的实际画面结构：

```
┌─────────────────────────────────┐
│  标题栏                          │
├─────────────────────────────────┤
│                                 │
│         （整片空白）              │  ← 原本的草稿区
│                                 │
├─────────────────────────────────┤
│  [任务卡片] HD 本机PC 正在压缩…   │
│  32/59   进度条                  │
│  ...Yuruichi_HD_30.jpg           │
│  PC 任务并发 32        [取消]     │
├─────────────────────────────────┤
│  正在压缩… · 本机 PC · PC 并发 1   │
└─────────────────────────────────┘
```

### 根因

草稿区的可见性绑定到了「窗口是否已经承办任务」，而不是「任务是否已经结束」：

```csharp
// src/ImgZip.Core/MainViewModel.cs:98
public bool ShowDraft => CurrentTask is null;
```

```xml
<!-- src/ImgZip.App/MainWindow.xaml:13 -->
<ScrollViewer Grid.Row="1" ... Visibility="{Binding ShowDraft, Converter={StaticResource BoolVisibility}}">
```

`CurrentTask` 在任务**被路由到本窗口的那一刻**就已赋值（`TaskCoordinator.AttachTask` → `SetPrimary`，`src/ImgZip.Core/MainViewModel.Tasks.cs:33`），也就是用户点下「开始压缩」的瞬间。于是 `ShowDraft` 立刻变 `false`，整块草稿区在第一帧就消失。

窗口布局是 `Auto / * / Auto` 三行（`MainWindow.xaml:6`）：

```xml
<Grid.RowDefinitions><RowDefinition Height="Auto" /><RowDefinition Height="*" /><RowDefinition Height="Auto" /></Grid.RowDefinitions>
```

第 1 行是 `Height="*"`，内容隐藏后仍占据全部剩余高度，因此显示为大片空白，而不是内容上移。

### 与设计意图的偏离

`docs/FEATURES-one-task-per-window.md`「关键约束：窗口是单次使用的」写的是：

> 任务结束后**不允许**在同一窗口里再开新任务——否则「一窗一任务」就退化成「一窗一串任务」……
> 这条要在 UI 上明确：**任务结束后**草稿区整体收起，只留结果区和「关闭」按钮。

设计意图是「**任务结束后**收起」，实现成了「**任务开始时**收起」。单次使用的语义只需要禁止「结束后再开第二个任务」，不需要在任务进行中隐藏用户刚确认过的设置。

### 影响

1. **任务进行中看不到自己选了什么。** 用户刚点完开始，想确认「我选的是收藏 JPEG 还是分享 WebP」「尺寸是多少」「输出到哪个目录」，全部不可见。任务跑几分钟时这个信息尤其有用。
2. **看不出源是什么。** 多窗口并行时，两个窗口长得一模一样（都只剩任务卡片），难以区分哪个是哪个。
3. **空白区占掉了窗口大部分面积**，而任务卡片被压在底部，进度条很窄。
4. **取消按钮位置偏远**，任务卡片在窗口最下方，而用户的注意力在刚消失的上方区域。

### 建议方案（未实施）

分三档，从保守到彻底：

**A. 任务进行中保持草稿区可见但只读**

把可见性判据从「有没有任务」改为「任务是否已确认终态」，并让编辑类控件在任务进行中禁用。

```csharp
// 概念示意，未实施
public bool ShowDraft => CurrentTask is not { TerminalConfirmed: true };
```

这样任务进行中草稿区照常显示（只读），任务结束后收起。

**实施前必须查证**：`CanEdit` 当前的实现是 `Coordinator.TasksReady`（`MainViewModel.cs:86`），而 `TasksReady` 只在 `InitializeAsync` 末尾 `MarkReady()` 时置 `true`（`TaskCoordinator.cs:148`）——它的语义是「启动恢复流程是否完成」，**不是**「是否空闲」。因此任务运行时 `CanEdit` 仍是 `true`，只改 `ShowDraft` 会让用户能在任务进行中改草稿。必须一并把 `CanEdit` 收紧为 `Coordinator.TasksReady && CurrentTask is not { TerminalConfirmed: false }`。

**改动面**：`ShowDraft` 一行 + `CanEdit` 一行 + 两处 `NotifyDerived` 名单已含这两个属性（无需改）+ 一处测试断言。

**测试影响**：`LifecycleTests.cs:611-617` 的 `window stops accepting new work once it owns a task` 当前断言「承办任务后 `ShowDraft` 立即变 `false`」，与方案 A 直接冲突。需改为断言语义：任务进行中「`ShowDraft` 为 `true` 但 `CanStartTask`/`CanEdit` 为 `false`」，任务结束后「`ShowDraft` 为 `false`」。单次使用语义（结束后不能再开新任务）保持不变。

风险：若某处依赖 `CanEdit` 表达「恢复完成」而非「可编辑」，收紧后行为会变。搜索 `CanEdit` 的绑定方（清空、添加图片、选择文件夹、预设按钮、引擎按钮、设置按钮、高级设置 Expander）均在草稿区内，收紧符合预期。

**B. 任务进行中收起草稿区，但在任务卡片里补全关键信息**

保留「开始即收起」的观感，改为把用户刚确认的设置压缩成一行摘要显示在任务卡片上。

改动面：`CompressionTaskViewModel` 增加只读摘要属性（如「收藏 JPEG · 90 质量 · 长边 4000 · 输出到 xxx」），卡片模板增加一行绑定。

优点：窗口更紧凑，任务进行中信息密度更高。
缺点：仍会「闪一下」，用户可能感觉界面跳变。

**C. 任务进行中把草稿区折叠成可展开的摘要条**

默认折叠成一行，点击可展开查看完整设置。

改动面：`Expander` 包住整个草稿区，绑定 `ShowDraft` 改为控制默认展开状态。

优点：兼顾紧凑与可查。
缺点：改动面最大，需要处理展开状态在多窗口间的记忆问题。

**倾向**：先做 **A**，因为它最贴近设计文档原意，且改动最小。若实施 A 后窗口显得过长，再考虑 B 或 C。

### 验收要点（实施时用）

- 任务进行中：草稿区可见，所有影响压缩结果的控件为只读
- 任务进行中：来源路径、预设、输出路径可读
- 任务结束后：草稿区收起，只剩结果与「打开输出」
- 结束后不能再在本窗口开第二个任务（单次使用语义不变）
- 多窗口时两个窗口的草稿区可以各自显示不同来源

---

## 附带发现：任务卡片在运行中显示「排队等待执行」

调查问题 1 时从同一张截图注意到的次要现象，一并记录。

任务卡片在状态为「正在压缩…」时，下方仍显示一行「排队等待执行」。

```csharp
// src/ImgZip.Core/CompressionTaskViewModel.cs:53
[ObservableProperty] private string resultSummary = "排队等待执行";
```

`ResultSummary` 的默认值就是「排队等待执行」，而它在整个生命周期中只在 `finished` 事件里被更新：

```csharp
// src/ImgZip.Core/CompressionTaskViewModel.cs:157
ResultSummary = Display.Summary(e);
```

因此在「已排队 → 正在运行」这段期间，它一直是初始默认值。任务真正结束（或取消、失败）后才会变成真实摘要。

严格说这不全是缺陷：`ResultSummary` 的语义本就是「结果摘要」，还没有结果时显示占位文案合理。但占位文案选了「排队等待执行」，在任务已经跑起来之后就会成为**错误的陈述**——用户看到状态写「正在压缩…」，下一行却写「排队等待执行」。

**建议**（已实施，见文末）：默认值改为空串，卡片模板对该行加可见性绑定。

---

## 实施记录（2026-11-13）

### 问题 1：采用方案 A

三处改动：

```csharp
// src/ImgZip.Core/MainViewModel.cs
public bool CanEdit => Coordinator.TasksReady && CurrentTask is null;
public bool ShowDraft => CurrentTask is not { TerminalConfirmed: true };
```

```xml
<!-- src/ImgZip.App/MainWindow.xaml：开始按钮改为独立判据 -->
<Button ... Visibility="{Binding ShowStartButton, Converter={StaticResource BoolVisibility}}" />
```

新增 `ShowStartButton => CurrentTask is null`：承办任务后「开始压缩」按钮消失，而不是留一个点不动的灰按钮；它不再与草稿区可见性共用判据。

**关键补充：模型层也要挡。** 只把按钮 `IsEnabled` 绑到 `CanEdit` 是不够的——拖放、右键 `--path` 转发都绕开按钮直接调 `SetSourcesAsync`。因此 `SetSourcesAsync` 和 `ClearSources` 开头都加了 `if (!CanEdit) return;`。这条是变异测试发现的：去掉守卫后 `Draft changed while a task was unresolved` 才会失败。

**`NotifyTasks` 必须补通知。** `ShowDraft` 依赖 `CurrentTask.TerminalConfirmed`，而任务走到终态时 `CurrentTask` 引用并没有变化，只在赋值那一刻通知过 `ShowDraft`。若不补，草稿区永远不会收起。`NotifyTasks` 现补发 `ShowDraft`、`CanEdit`、`CanStart`、`CanStartTask`、`ShowStartButton`。

### 附带发现：ResultSummary 占位文案

```csharp
[ObservableProperty] private string resultSummary = "";      // 原为 "排队等待执行"
public bool HasResultSummary => !string.IsNullOrEmpty(ResultSummary);
```

卡片模板对该行加 `Visibility` 绑定。新增测试 `a running task carries no result summary yet` 覆盖「新建→运行中→终态」三个阶段。

### 测试调整

三个既有用例因语义变更而调整（不是放宽断言，是改成断言新的正确行为）：

| 用例 | 原断言 | 现断言 |
| --- | --- | --- |
| `window stops accepting new work once it owns a task` → 更名 `window keeps its draft visible but read-only while the task runs` | 承办任务后 `ShowDraft` 立即为 `false` | 运行中 `ShowDraft` 为 `true` 但 `CanEdit`/`CanStartTask`/`ShowStartButton` 均为 `false`；结束后 `ShowDraft` 为 `false` |
| `draft remains enabled while submitted request is immutable` → 更名 `draft is frozen while running and the submitted request stays immutable` | 运行中 `CanEdit` 为 `true` | 运行中 `CanEdit` 为 `false`，且模型层拒绝改来源 |
| `copy preference applies to folders but not file sources` | 同一窗口跑两个任务 | 改为两个窗口各跑一个任务（一窗一任务） |

### 变异验证

四项，全部被抓住：

| 变异 | 报错 |
| --- | --- |
| `ShowDraft` 改回 `CurrentTask is null`（重现原问题） | `Draft should be visible but frozen while a task is unresolved` |
| `CanEdit` 去掉 `CurrentTask is null` | 同上 |
| `ResultSummary` 默认值改回「排队等待执行」 | `Fresh task should not claim a result yet` |
| 去掉 `SetSourcesAsync` / `ClearSources` 的 `!CanEdit` 守卫 | `Draft changed while a task was unresolved` |

第三项在第一轮变异中**没有被抓住**（当时还没有对应测试），补测试后重测才抓住——记录在此，说明当时的测试确实存在盲区。

### 实机验证

20 张 / 226 MB 目录，任务进行中（3 秒、进度 16/20）截图确认：

- 来源卡片、复制选项、三个预设、执行位置、高级设置、输出路径**全部可见**
- 「清空」「添加图片」「选择文件夹」「本机 PC」「NAS」**全部禁用**
- 「开始压缩」按钮**已消失**
- 任务卡片正常显示进度

任务结束后草稿区收起，只剩任务卡片与「打开输出」，与预期一致。

71 组测试通过。

### 附带的第二项：消息两处重复 —— 已修复

任务运行时同一句进度文案出现两次（顶部 InfoBar 与任务卡片内各一次）：

```
InfoBar:   正在建立文件清单
任务卡片:  正在建立文件清单
```

原因是 `Mirror` 把 `task.Message` 抄给了 `MainViewModel.Message`（`MainViewModel.Tasks.cs:40`），而这两处分别绑定：

```xml
<InfoBar Message="{Binding Message}" />                          <!-- 窗口层的 Message -->
<TextBlock Text="{Binding Message}" />                            <!-- 任务卡片，DataContext=CurrentTask -->
```

**修复**：`Mirror` 不再镜像 `Message`。两个 `Message` 各有归属：

| 位置 | 归属 | 内容 |
| --- | --- | --- |
| 任务卡片内 | 任务 | 进度与诊断（「正在建立文件清单」「正在压缩第一批」…） |
| 顶部 InfoBar | 窗口 | 与任务无关的提示（配置读不出、来源不合法、强制关闭清不干净） |

这不只是观感问题。镜像还有一个更严重的后果：任务每次发进度事件都会**覆盖窗口级错误提示**——用户看到「配置无法读取，请检查设置」，任务一开始跑就被顶掉，再也看不到。一条本该留着的错误被一条「正在进行」顶掉，这才是真正该修的。

新增测试 `task progress lives on the card and window notices stay on the window` 同时锁住这两点：任务消息不进窗口层，以及窗口级提示不被任务事件覆盖。

**`ResultSummary` 不镜像，但保留窗口级的草稿文案。** 任务卡片用自己的 `ResultSummary`（终态才有值），窗口底部的 `ResultSummary`（「原始图片保持不变」）绑定了 `ShowDraft`，草稿收起后随之隐藏，避免与任务卡片的结果摘要重复显示同一句。

### 验证

71 → 72 组测试通过。变异：把 `Message = task.Message` 加回 `Mirror` → `Window surface echoed a task message: '正在建立文件清单'`，被抓住。

实机验证（24 张 / 270 MB）任务进行中：进度文案只在任务卡片出现一次，顶部 InfoBar 不显示任务消息，窗口底部草稿文案正常显示「原始图片保持不变」。

---

## 与既有文档的关系

- 问题 1 是 `docs/FEATURES-one-task-per-window.md` 阶段 3 的实现偏离设计意图，不是新功能需求。已在该文档阶段 3 条目补一条说明：草稿区的收起时机以「任务结束」为准。
- `LifecycleTests.cs` 中原 `window stops accepting new work once it owns a task` 已按新语义改写并更名（见上表）。
