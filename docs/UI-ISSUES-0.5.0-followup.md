# ImgZip 0.5.0 后续问题记录

记录日期：2026-11-12

状态：问题 1、2、3 均已修复并实机验证。

来源：用户实机使用 0.5.0 安装版时提交的截图与描述。

| # | 问题 | 状态 |
| --- | --- | --- |
| 1 | 任务**开始**后上方界面全部消失 | ✅ 已修复（任务进行中可见只读） |
| 1b | 任务卡片在运行中显示「排队等待执行」 | ✅ 已修复 |
| 1c | 消息在 InfoBar 与任务卡片重复，且覆盖窗口级错误 | ✅ 已修复 |
| 2 | 任务**完成**后上方信息消失（问题 1 只修了一半） | ✅ 已修复（草稿区常驻，承办任务后只读） |
| 3a | 无空白草稿时二次启动新开空窗口 | ✅ 已修复（改为叫回已有窗口） |
| 3b | 多窗口时点任务栏图标无法呼出窗口 | ✅ 已修复（激活任一窗口即整组还原；见该节的「能做到什么」） |

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

`docs/FEATURES-one-task-per-window.md`「关键约束」一节（当时标题为「窗口是单次使用的」，现已改为「一个窗口只承办一个任务」）写的是：

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

## 问题 2：任务完成后上方信息消失（问题 1 只修了一半）

记录日期：2026-11-13（2026-11-21 实施）
状态：**已修复。** 见文末「实施记录（2026-11-21）」。

### 现象

任务完成后，草稿区（来源、预设、执行位置、高级设置、输出路径）**仍然整体消失**，只剩任务卡片。

用户描述：「任务完成后上方信息消失，也就是说本次修复只修复了一半。」

截图确认（18/18 完成、节省 90.1% 的那次运行）：窗口上半部分是整片空白，只有底部任务卡片与摘要行。

### 与问题 1 的关系

问题 1 修的是「任务**开始**时草稿区消失」，现在任务进行中草稿区保留。但草稿区在「任务**结束**时」仍会消失，而这一行为是**按当前设计文档实现的**：

```csharp
// src/ImgZip.Core/MainViewModel.cs:106
public bool ShowDraft => CurrentTask is not { TerminalConfirmed: true };
```

`docs/FEATURES-one-task-per-window.md`「关键约束」一节（当时标题为「窗口是单次使用的」）明确写：

> 任务结束后**不允许**在同一窗口里再开新任务……这条要在 UI 上明确：**任务结束后草稿区整体收起**，只留结果区和「关闭」按钮。

所以当前行为符合设计文档，但**不符合用户预期**。

### 这是个设计冲突，不是缺陷

两条诉求互相矛盾：

| 诉求 | 来源 | 内容 |
| --- | --- | --- |
| A | 设计文档（单次使用） | 任务结束后收起草稿区，防止用户在本窗口开第二个任务 |
| B | 用户 | 任务结束后仍要能看到上方信息（本次来源、预设、输出路径） |

设计文档的担忧是真实的：如果任务结束后草稿区恢复可用，用户会理所当然地在同一窗口再点一次「开始压缩」，于是「一窗一任务」退化成「一窗一串任务」，整个重构的意义消失。

但用户想看的只是信息，不是想开第二个任务。**「可见」和「可再启动」是两件不同的事**——问题 1 的修复已经证明了这一点：任务进行中草稿区可见但只读，既不丢信息也不会误开第二个任务。同样的处理方式在任务结束后依然成立。

### 建议方案（未实施）

把 `ShowDraft` 的判据从「任务未确认终态」改为「本窗口从未承办任务」，即回到最初：

```csharp
// 概念示意，未实施
public bool ShowDraft => CurrentTask is null;
```

但**必须同时保证**草稿区在任务结束后仍不可编辑、不可再启动：

```csharp
public bool CanEdit => Coordinator.TasksReady && CurrentTask is null;   // 已实现，无需改
public bool ShowStartButton => CurrentTask is null;                    // 已实现，无需改
```

现有代码里这两个判据**已经是**「有没有承办过任务」，而不是「任务是否结束」。也就是说，只改 `ShowDraft` 一行，草稿区就会在任务结束后继续显示，而里面的控件全部保持禁用、「开始压缩」按钮保持消失——单次使用语义不受影响。

这是本方案最有说服力的地方：**防误开的守卫已经落在了 `CanEdit` 和 `ShowStartButton` 上，`ShowDraft` 只管可见性。** 三者分离后，可见性与可用性不再被同一行代码绑死。

**代价与风险**：

1. 窗口会一直显示完整草稿区，即使任务已完成。视觉上「结果」和「草稿」并存，用户可能疑惑「这里是还能再压一次吗」。需要靠控件禁用状态和「开始压缩」缺失来传达「不能」。
2. 之前为问题 1 写的测试 `window keeps its draft visible but read-only while the task runs` 断言「任务结束后 `ShowDraft` 为 `false`」，需改为断言「结束后仍可见、但仍不可编辑」。`task summary...` 系列中依赖草稿收起隐藏底部 `ResultSummary` 的绑定也需复核（见下）。
3. 窗口底部的 `ResultSummary`（「原始图片保持不变」）当前绑定 `ShowDraft` 用于隐藏。草稿区不再收起后，它会一直显示，与任务卡片的结果摘要并存，需要重新决定这一行是否还该显示、显示什么。

### 用户的决定（2026-11-13）

**保留上方信息可读，看完结果后自行关闭窗口。** 不做精简摘要，不做卡片内摘要。

原话：

> 目前窗口不会自己关，任务完成后残留窗口，但是一大片空白就很怪，所以我建议还是保留上方的信息可读，我看完结果之后自行关闭窗口就好

决策要点：

1. **采纳建议方案**：`ShowDraft` 改回「本窗口从未承办任务」，任务结束后草稿区继续显示。
2. **不砍草稿区内容**：完整显示，不做精简摘要，不把信息塞进任务卡片。
3. **不做自动关窗**：窗口留着，用户看完自己关。
4. 「可见但永久只读」这个状态**可接受**——用户明确知道任务已完成、不会再压一次，只是要能回看设置。

这同时回答了上面「待用户确认」的三条：

| 问题 | 决定 |
| --- | --- |
| 完整草稿区还是精简摘要 | **完整草稿区** |
| 底部「原始图片保持不变」还要吗 | 待实施时再定；倾向于**换掉**——任务完成后草稿已不是「未开始」状态，这句话失真 |
| 接受「可见但永久只读」吗 | **接受** |

### 用户决定带来的推论

用户那句「一大片空白就很怪」点出了问题的实质：**残留窗口不是问题，空白才是问题**。

如果窗口会自动关闭，收起草稿区是合理的——窗口本来就不该存在。但窗口不会自己关，于是收起草稿区的结果是留下一个占着屏幕、只有底部一条任务卡片的空壳。用户要么去看结果、要么去关窗口，而中间那 60% 的空白区域对这二者都毫无贡献。

所以真正需要判断的不是「窗口该不该单次使用」，而是「单次使用要不要表现为视觉上的空」。答案是不要：单次使用是**行为约束**（不能再启动第二个任务），不该是**视觉惩罚**（把已经确认过的信息抹掉）。

### 实施时的注意点（未实施）

1. **只改一行** `ShowDraft`：

   ```csharp
   public bool ShowDraft => CurrentTask is null;
   ```

   `CanEdit` 与 `ShowStartButton` 已经是「有没有承办过任务」的判据，无需改动，单次使用语义不受影响。

2. **测试要改**：`window keeps its draft visible but read-only while the task runs` 当前断言「任务结束后 `ShowDraft` 为 `false`」，需改为「结束后仍为 `true`，且 `CanEdit`/`ShowStartButton` 仍为 `false`」。

3. **窗口底部 `ResultSummary` 的处理**：它现在绑 `ShowDraft` 用来隐藏，草稿区不收起后它会一直显示「原始图片保持不变」——任务完成后这句话不成立。需要决定：置空、换成任务结果、或干脆删掉这一行（任务卡片已经显示结果摘要）。

4. **文档要改**：`FEATURES-one-task-per-window.md`「关键约束：窗口是单次使用的」中「任务结束后草稿区整体收起」需改写为「草稿区常驻显示、承办任务后只读，不可再启动新任务」。（已实施：标题改为「一个窗口只承办一个任务」，`ShowDraft` 恒为 `true`。）

### 界面示意（实施后预期）

```
┌─────────────────────────────────┐
│  图片压缩 / 主题 / 设置           │
│  [来源卡片：这一批的来源]（灰）    │
│  [复制选项 / 预设 / 执行位置]（灰）│
│  [高级设置 / 输出路径]（灰）       │
│                                 │
├─────────────────────────────────┤
│  [任务卡片] 已完成 18/18          │
│  成功 18 · 节省 90.1%  [打开输出] │
└─────────────────────────────────┘
```

上半屏显示「这次压的是什么、用什么参数、输出到哪」，全部只读；下半屏显示结果与「打开输出」。用户看完点 X 关闭。

### 遗留的另一个候选方案（不采用）

曾考虑「保持收起 + 把关键信息摘进任务卡片」。用户明确要求保留完整上方信息，此方案不采用，记录备查。

### 实施记录（2026-11-21）

**`ShowDraft` 从「任务未确认终态」改为恒 `true`。** 不是改回 `CurrentTask is null`——那正是问题 1 的实现，会把「开始即空白」原样搬回来。草稿区现在是窗口的常驻框架，任务进行中、结束后都显示：

```csharp
// src/ImgZip.Core/MainViewModel.cs（返回值刻意是常量：一条被 XAML 与测试共同引用的具名不变量）
public bool ShowDraft => true;
public bool ShowDraftHint => ShowDraft && CurrentTask is null;   // 新增：底部草稿文案
```

单次使用由既有判据承担，一行未改：`CanEdit => Coordinator.TasksReady && CurrentTask is null`、`ShowStartButton => CurrentTask is null`。`SetSourcesAsync` / `ClearSources` 开头的 `if (!CanEdit) return;` 仍在，所以拖放与 `--path` 转发也挡得住。

**`ResultSummary` 那一行：只在窗口从未承办任务时显示。** 底部 `MainWindow.xaml` 的「原始图片保持不变」原绑 `ShowDraft`（靠草稿收起隐藏），现在改绑新的 `ShowDraftHint`。任务一承办它就隐藏；结束后任务卡片已自述「成功 N · 节省 X%」，这里不重复、不臆造第二个摘要。`ShowDraftHint` 已加入 `NotifyDerived()` 名单与 `NotifyTasks()` 的补发列表——它依赖 `CurrentTask` 是否为空，而任务路由到窗口时引用才会变。

**测试调整。**

| 用例 | 原断言 | 现断言 |
| --- | --- | --- |
| `window keeps its draft visible but read-only while the task runs` → 更名 `window keeps its draft visible but read-only once it owns a task` | 结束后 `ShowDraft` 为 `false` | 结束后 `ShowDraft` 仍为 `true`，且 `CanEdit`/`CanStartTask`/`ShowStartButton` 均为 `false` |
| `Program.cs` 的 `unknown task retained; …`（:128） | `!vm.ShowDraft` | `vm.ShowDraft && !vm.ShowDraftHint && !vm.CanEdit && !vm.ShowStartButton` |
| 新增 `draft stays visible and read-only for the window's whole life` | — | 起始可见 + 有文案 → 运行中可见但冻结、文案消失 → 完成后仍可见 → 改来源被拒 → 再点开始无效 |

**变异验证**（全部被抓住）：

| 变异 | 报错 |
| --- | --- |
| `ShowDraft => CurrentTask is not { TerminalConfirmed: true }`（问题 2 原样） | `Draft did not stay visible and read-only after the task was confirmed`（Program.cs:128）；屏蔽该行后为新用例的 `Draft disappeared after the task finished` |
| `ShowDraft => CurrentTask is null`（问题 1 原样） | `Draft should be visible but frozen while a task is unresolved`（Program.cs:120）；屏蔽该行后为新用例的 `Draft vanished the moment a task started` |
| `CanEdit` 去掉 `CurrentTask is null` | `Draft should be visible but frozen while a task is unresolved`；屏蔽该行后为 `Draft changed while a task was unresolved` |

第一项第一轮先命中的是 `Program.cs` 那条断言（测试按文件顺序执行），为了看清新用例的报错信息，临时放宽了该行，确认后再恢复。

**验证**：72 → 73 组通过（+1 草稿常驻用例；同期另有 1 组召回策略用例，全量现为 74 组）；`dotnet build src/ImgZip.App/ImgZip.App.csproj -c Release` 成功，0 错误（1 条 `CS8603` 在 `SettingsDialog.xaml.cs`，为既有告警，与本次改动无关）。

**实机验证**（12 张 / 135.3 MB → 24.8 MB，节省 81.7%）：任务完成后，

| 指标 | 结果 |
| --- | --- |
| 草稿区五要素（来源 / 压缩预设 / 执行位置 / 高级设置 / 输出到新文件夹） | **5/5 保留**，全部灰化只读 |
| 底部「原始图片保持不变」 | **0 次**（已隐藏）✅ |
| 「开始压缩」按钮 | 已消失 ✅ |
| 「打开输出」按钮 | 在 ✅ |
| 任务卡片 | 成功 12 · 失败 0 · 跳过 0 · 节省 81.7% ✅ |

截图：`artifacts/issue2-after.png`。这正是用户要的效果——上半屏能回看「这次压的是什么、用什么参数、输出到哪」，下半屏看结果，看完自己关窗。

---

## 问题 3：多窗口时点任务栏图标无法呼出窗口

记录日期：2026-11-14（问题 3a / 3b 于 2026-11-21 修复）
状态：**已修复。** 但「一次点击就还原全部」在系统层面做不到，实际效果见「能做到什么」——这一点必须向用户讲清。

用户描述：「多窗口存活的时候，点击任务栏图标没法呼出全部窗口，我建议是点击可以直接呼出所有窗口在台前。」

### 已复现

用正式安装版（`0.5.0+f04769...`）实测，**单进程多窗口**，全部最小化后点击任务栏上的 ImgZip 按钮：

| 窗口数 | 点击后还原数 |
| --- | --- |
| 1 | **1 / 1** ✅ |
| 2 | **0 / 2** ❌ |
| 3 | **0 / 3** ❌ |

判定依据是 `GetWindowPlacement().showCmd`（`2` = 最小化，`1` = 正常），不是目测。

**一个窗口时正常，两个以上就完全失效** —— 这正是用户的描述。

### 根因

**不是「没还原全部」，而是「一个都没还原」。**

Windows 11 默认合并同类任务栏按钮，同一个 AUMID 的所有窗口共用一个按钮。ImgZip 从未调用 `SetCurrentProcessExplicitAppUserModelID`，AUMID 由 exe 路径派生，所以**同进程所有窗口必然合并成一个按钮**（已用截图确认：3 个 ImgZip 窗口在跑，任务栏只有一个紫色图标）。

窗口数 ≥ 2 时，点击这个合并按钮触发的是**缩略图浮层**而不是激活窗口：

```
悬停/点击 → TaskListThumbnailWnd（三张缩略图）
                ↓
         还需要再点一次某张缩略图
```

实测点击后 `showCmd` 仍是 `MIN,MIN,MIN`，前台窗口是 `Shell_TrayWnd`（任务栏自己）——说明这一下点击被任务栏吃掉了，只用来弹浮层。

截图证据：3 个窗口最小化后悬停任务栏按钮，浮层里并排三张缩略图（来源分别是 `imgzip-taskbar-a/b/c`）。

### 应用侧完全没有补偿逻辑

```powershell
# 在 src/ 下搜索窗口过程钩子与激活消息，结果为空
WM_ACTIVATEAPP | SC_RESTORE | SetWindowSubclass | SetWindowLongPtr
GWLP_WNDPROC  | WM_SYSCOMMAND | TaskbarButtonCreated
```

`BringToFront` 只在三个时机被调用，全都是**新建窗口**的时候：

| 位置 | 时机 |
| --- | --- |
| `App.xaml.cs:61` | 无参启动且找到空白草稿 → 置前该窗口 |
| `App.xaml.cs:71` | 收到转发的空消息 → 置前空白草稿 |
| `App.xaml.cs:91` | 新建窗口后置前 |

没有任何一条路径会在「用户点任务栏按钮」时被触发——**因为点击任务栏按钮根本不启动进程**，`OnLaunched` 不会跑，`InstanceBroker` 也不参与。这条链路完全在 Explorer 手里。

### 关键认识：这件事没有系统级开关

Windows 没有「点击分组按钮时还原该组全部窗口」的官方 API，也没有对应的用户设置（任务栏设置只有「合并 / 不合并」）。连资源管理器自己都做不到——同一天在 Microsoft Q&A 上有用户在问同样的问题，得到的答复是「改成不合并任务栏」或「用任务视图」，没有程序化方案。

所以这不是「漏调了一个 API」，而是**必须在应用内自己补偿**的行为。

### 四个候选方案

#### 方案 A：每个窗口独立 AUMID（任务栏多个按钮）

给每个窗口调用 `SetCurrentProcessExplicitAppUserModelID` 创建一个独立 AUMID，任务栏就不再合并，一个窗口一个按钮，点哪个恢复哪个。

- **优点**：实现简单可靠，不依赖钩子，行为符合 Windows 原生预期。
- **缺点**：**不满足用户诉求**。用户要的是「点一下全部到台前」，这个方案是「点哪张显哪张」，窗口多时任务栏按钮还会爆炸（一窗一任务模型下很容易开到 5–8 个）。
- 结论：**不采用**。它解决的是「分不清哪个窗口」，而用户的问题是「一个都出不来」。

#### 方案 B：子类化窗口过程，激活时恢复兄弟窗口 ★ 推荐

给每个 `MainWindow` 的 `WndProc` 挂钩子（`SetWindowSubclass`），在 `WM_ACTIVATEAPP`（`wParam != 0`，本进程被激活）时，遍历 `App.windows` 里的其余窗口，对仍处于最小化的调用 `ShowWindow(SW_RESTORE)`。

- **优点**：直接达成用户诉求；不依赖任何未公开 API；任务栏怎么合并都无所谓，因为触发点是「本进程被激活」。
- **风险一：误伤**。用户正常操作时也会触发 `WM_ACTIVATEAPP`——比如从浏览器切回 ImgZip 的某一个窗口，此时若把其他被**故意**最小化的窗口也全拉起来，就是打扰。
  - 缓解：只在这些窗口「本来就是被一起最小化的」时候才恢复。维护一个已最小化计数（`WM_SIZE` 的 `SIZE_MINIMIZED`/`SIZE_RESTORED` 增减），仅当「除刚激活的这个之外，其余全部处于最小化」时才批量恢复。用户手动只最小化 A、B 和 C 都开着的情况不会命中（计数 1/3，不是全部分）。
- **风险二：时序**。`WM_ACTIVATEAPP` 到达时，被点击的那个窗口是否已经完成还原，需要实测确认；若此时它仍是 `IsIconic`，简单跳过它即可。
- **风险三：WinUI 3**。`Window` 的 HWND 要通过 `WinRT.Interop.WindowNative.GetWindowHandle` 拿，`SetWindowSubclass` 需要 `comctl32.dll`；WinUI 3 窗口能否安全子类化需先做一次小试验证，`Window` 类的消息循环不完全由应用代码拥有。

#### 方案 C：`RegisterShellHookWindow` 监听 `HSHELL_WINDOWACTIVATED`

注册 shell hook 接收 Explorer 的窗口激活通知，识别「激活来自任务栏」后统一恢复。

- **优点**：比子类化更贴近「任务栏点击」这一语义。
- **缺点**：`HSHELL_WINDOWACTIVATED` 不携带「激活来源」信息，仍然要靠状态推断；且 shell hook 在 WinUI 3 / 现代 Windows 上文档支持度低，可靠性不如方案 B。
- 结论：不如方案 B 直接。

#### 方案 D：不合并 + 首窗口优先（折中）

保持合并，但改善「点一次出不来」的体验：让被任务栏恢复的那个窗口始终是**空白草稿**（或最近完成的那个），而不是随机某一个。

- **优点**：改动极小，只需在窗口激活时调整 Z 序。
- **缺点**：仍然只出一个窗口，与用户明确诉求「全部到台前」不符。
- 结论：可作为方案 B 失败时的退路。

### 能做 / 不能做什么（实施后的诚实结论）

原型实测（`artifacts/proto/log.txt`）得出一个此前未料到的事实，必须写清楚：

**裸点合并后的任务栏按钮，不会激活任何窗口，因此不会给应用任何通知。**

`10:26:54.222/.252/.253` 三次记录里，点击那一刻只看到三个 `WM_GETICON`，没有 `WM_ACTIVATEAPP`，
连一条窗口消息都没有。任务栏只负责弹出缩略图浮层，焦点留在 `Shell_TrayWnd` 上。

也就是说「点一下按钮 → 全部窗口出来」**没有可用的触发点**：应用能收到的最早信号是
「某个窗口正在被激活」，而裸点击不产生这个信号。这不是实现取舍，是系统边界。

所以实际做到的是：

| 用户动作 | 行为 |
| --- | --- |
| 点任务栏按钮（裸点一次） | 弹出缩略图浮层（系统行为，无法改变） |
| 点浮层里的任一张缩略图 | ✅ **全部最小化窗口一起还原** |
| Alt+Tab 切到某个最小化的 ImgZip 窗口 | ✅ **全部最小化窗口一起还原** |
| 双击图标 / 再点快捷方式（无来源） | ✅ 见 3a，已有窗口整体还原并置前 |
| 用鼠标点另一个本来就没最小化的 ImgZip 窗口 | 不动其他窗口（闸门不成立，见下） |

比修复前（一个都回不来）是决定性的改善，但仍比用户原话「点一下全部到台前」少一次点击。

### 实施：`WindowReveal` + 双条件闸门

新增 `src/ImgZip.App/WindowReveal.cs`，给每个窗口的 HWND 挂 `SetWindowSubclass`，
在 `WM_ACTIVATEAPP`（`wParam != 0`）时判断是否要还原兄弟窗口。

**闸门是两条，缺一不可：**

```csharp
GetForegroundWindow() == hWnd && IsIconic(hWnd)
```

第一版只有 `IsIconic(hWnd)`，实测立刻被反例抓住：

```
最小化 #[0] → 点击 #[1]
  ✗ 最小化窗口数变成 0 —— 闸门会误伤
```

原因是 `WM_ACTIVATEAPP` 按**进程**广播，每个顶层窗口都会收到一份。用户故意把 A 最小化、
正在用 B 时，A 也会收到该消息，只看 `IsIconic(A)` 就会把 A 拉起来。
补上「前景窗口就是自己」之后，范围才收窄到「这一个窗口正在从最小化恢复」：

| 场景 | 结果 |
| --- | --- |
| 全部最小化 → 激活其中一个 | 3/3 还原，+4s 仍保持 ✅ |
| 故意最小化 A → 真实点击 B | A 仍最小化 ✅ |

**递归**：还原兄弟窗口会再触发一轮 `WM_ACTIVATEAPP`，但那时它们已经不是最小化了，
闸门不成立，天然收敛。仍保留一个 `revealing` 布尔兜底，防止将来有人加「顺带还原隐藏窗口」。

**清理**：窗口 `Closed` 时 `RemoveWindowSubclass`，原型里 4 窗口 × 2 层子类同时存活未出现
崩溃或泄漏，确认 WinUI 3 不会在启动后覆盖钩子（自发 `WM_APP+0x51` 在 +4s/+15s/+45s 均收到）。

**实现取舍**：`RevealSiblings()` 只还原、不抢前台——前台交给用户点中的那个窗口，
应用不该反过来决定焦点去哪。

---

### 附带发现：无空白草稿时，二次启动会新开空窗口

检查过程中发现的独立问题，与任务栏无关但同属「窗口呼回」范畴。

`App.xaml.cs:58` 的无参启动分支：

```csharp
if (paths.Length == 0 && FindBlankDraft() is { } blank)   // 找到空白草稿才复用
    BringToFront(blank);
else
    OpenWindow(worker, store, paths);                      // 找不到就新开一个
```

`FindBlankDraft` 的条件是「草稿在显示 **且没有来源**」。所以当所有窗口都已经有来源（最普通的场景：压了三批图）时再双击图标，结果是**新开一个空窗口**，已有窗口一个都不动。

实测：

| 已有窗口 | 二次启动后 |
| --- | --- |
| 3 个都有来源 | 变成 **4 个**，新开的是空窗口，原来 3 个仍最小化 |
| 2 有来源 + 1 空白草稿 | 仍是 3 个，只有空白草稿被还原 |

**这个行为值得单独确认是否合理。** 从「不堆空窗口」的设计意图看，找不到空白草稿时更应该把已有窗口叫出来，而不是再造一个空壳。

#### 已修复（2026-11-21）

把「该做什么」抽成纯函数 `src/ImgZip.Core/RecallAction.cs`：

```csharp
public static RecallAction Decide(int windowCount, bool hasBlankDraft) =>
    windowCount == 0  ? RecallAction.Open
    : hasBlankDraft   ? RecallAction.FocusDraft
    :                   RecallAction.RevealAll;
```

`App.xaml.cs` 的两处「无来源启动」都改为调用 `Recall()`，由它执行这个决定；
`RevealAll()` 还原所有最小化窗口并把最后打开的窗口置前。抽成纯函数是因为真正的窗口操作
没法在单元测试里跑，而「该做哪一件」正是这里唯一容易搞错的地方。

**顺带修正 `FindBlankDraft()` 的判据。** 它原来是 `ShowDraft && !HasSources`；问题 2 之后
`ShowDraft` 恒为真，条件会退化成 `!HasSources`，让「承办过任务、来源已清空」的窗口被当成
可复用空白草稿。改为 `!HasCurrentTask && !HasSources`，即「还没承办过任务」。

**新增测试** `tests/ImgZip.Tests/RecallTests.cs`（6 条断言，覆盖三个分支，其中两条专门钉住
「无空白草稿 + 有窗口 → RevealAll」这个曾经的错误分支）。

**实机验证**（3 个窗口都有来源，全部最小化，无参数再启动一次）：

| 指标 | 修复前 | 修复后 |
| --- | --- | --- |
| 窗口数 | 3 → **4**（新开空壳） | 3 → **3** ✅ |
| 还原数 | 0 / 4 | **3 / 3** ✅ |
| 前台 | 新开的空窗口 | 已有窗口 ✅ |

### 当时的建议实施顺序（已按此执行）

1. 先修附带发现（低成本、无风险）：无空白草稿时不再新开窗口，改为把已有窗口全部还原并置前——这条路径不涉及窗口过程钩子，纯应用逻辑。**已完成。**
2. 再评估方案 B。**实施前必须先做一个原型**验证 WinUI 3 窗口能否安全 `SetWindowSubclass` 并收到 `WM_ACTIVATEAPP`。**已做，结论：可行**（`artifacts/proto/log.txt`，含 3 次跑批与一次反例对照）。

### 验证手段备注

自动化环境无法「真的用鼠标点任务栏」，但可以用如下方式稳定复现：

1. `ShowWindow(SW_MINIMIZE)` 最小化全部窗口；
2. `SetCursorPos` + `mouse_event(LEFTDOWN/LEFTUP)` 点击任务栏按钮坐标；
3. `GetWindowPlacement().showCmd` 读回状态。

单窗口场景用此法能稳定跑通（点击后 `MIN → NRM`），说明点击注入本身有效，多窗口下 `MIN,MIN,MIN` 保持不动的结果可信。

另外注意：本机任务栏按钮坐标需先定位——UIA 在 Win11 任务栏上取不到按钮（`MSTaskListWClass` 的子元素数为 0），只能靠截图找图标颜色（ImgZip 图标底色 `#7B3F8C`）反推 x 坐标。

---

## 与既有文档的关系

- 问题 1 是 `docs/FEATURES-one-task-per-window.md` 阶段 3 的实现偏离设计意图，不是新功能需求。已在该文档阶段 3 条目补一条说明：草稿区随任务状态收起，两次都踩过坑。
- 问题 2 则反过来：**当时的实现符合设计文档，但设计文档的这条约定本身已被用户否决**。实施后 `FEATURES-one-task-per-window.md`「关键约束」一节的标题改为「一个窗口只承办一个任务」——约束的对象是任务而非窗口寿命：窗口可以一直存在，只是不能再接第二个任务；「任务结束后草稿区整体收起」一句已改写为「草稿区常驻显示、承办任务后只读，单次使用靠禁用编辑与移除启动入口保证」。
- `LifecycleTests.cs` 中原 `window stops accepting new work once it owns a task` 已按新语义改写并更名两次，现为 `window keeps its draft visible but read-only once it owns a task`；问题 2 另新增 `draft stays visible and read-only for the window's whole life`。
- 问题 3 的实现落在 `src/ImgZip.App/WindowReveal.cs`（新增，窗口子类化）与 `src/ImgZip.App/App.xaml.cs`（`Recall` / `RevealAll` / `RevealSiblings` / 收紧后的 `FindBlankDraft`）；决策表在 `src/ImgZip.Core/RecallAction.cs`，测试在 `tests/ImgZip.Tests/RecallTests.cs`。
- 问题 3b 是唯一一个**没有完全达成用户原话**的修复：裸点任务栏按钮不会激活任何窗口，因此没有可用的触发点。已在「能做 / 不能做什么」一节如实记录，避免后人把它当成 bug 再查一遍。
