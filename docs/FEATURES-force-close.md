# 强制关闭：点 X 直接终止执行层

**状态：已实现，0.4.0 开发线。** 代码在工作区完成并通过 54 组检查，尚未提交、尚未发版（README 记载的已发布版本仍是 0.3.6）。未做真实 NAS 强制关闭与安装包验收。

## 背景

旧流程在 `Window_Closing` 里弹一个三选对话框：「取消并等待 / 仅关闭窗口 / 返回」。两个问题：

1. **打断高频操作**。关窗是收尾动作，不该要求用户在一段说明文字里做选择。
2. **更根本的问题：旧实现无法确定任务的死活**。任务记录里只有 `queued` / `launching` / `running` / `finished` 四个阶段，没有任何进程信息。所以「仅关闭窗口」之后重启，只能显示「状态待确认」，要求用户点「重新检查」；而重检只是重读 journal 或问远端，**无法证明进程已停止**。用户点一次「重新检查」，程序也只是换一种方式继续猜。

本次改动的核心不是删掉那个对话框，而是把「猜」换成「查」：关窗时先把执行层进程杀掉，杀不掉就把 PID 与启动时刻写进记录，下次启动用进程事实定论。

## 核心设计：用进程事实代替主观猜测

一条任务是否还在跑，由两个字段回答：

```csharp
// src/ImgZip.Core/TaskStore.cs:14-17
public int WorkerPid { get; init; }
public DateTimeOffset WorkerStartedAt { get; init; }
```

三个写入点，覆盖三种崩溃时机：

| 时机 | 写入者 | 位置 |
| --- | --- | --- |
| 进程刚启动（正常路径） | 主程序读 `process.Id` / `process.StartTime` | `src/ImgZip.Core/WorkerClient.cs:36` |
| 协调者收到事件后补写记录 | `TaskCoordinator.RememberProcess` | `src/ImgZip.Core/TaskCoordinator.cs:34-43` |
| 主程序在收到事件前就崩了 | worker 自己写 `jobs/<jobId>/worker.pid` | `worker/imgzip-worker.ps1:513-519` |

判定统一走 `IProcessProbe.IsAlive(pid, startedAt)`（`src/ImgZip.Core/IProcessProbe.cs:6-13`），实现要求**三个条件同时成立**：PID 存在、进程未退出、`StartTime` 与记录相差不超过 2 秒（`src/ImgZip.Core/SystemProcessProbe.cs:12,14-32`）。2 秒容差用于吸收不同 API 的精度差异与本地时区换算。

**为什么必须带启动时刻**：PID 会被复用。只看 PID，会把「另一个进程恰好拿到同一个号」误判成任务还在跑，于是名额永远不释放、任务永远显示「状态待确认」。这是旧实现最隐蔽的一处不确定性。

无法读取启动时刻时（worker 在受限环境读不到自身 `StartTime`，记录里留空），只能按 PID 判活并接受这个风险，代码里写明了这一降级（`SystemProcessProbe.cs:21-22`）。

## 强制关闭的执行流程

入口是 `Window_Closing`（`src/ImgZip.App/MainWindow.xaml.cs:263-281`）：

1. 保存窗口位置；若已在关闭流程中或没有未结束任务，直接放行，**不弹任何对话框**（`:265-268`）。
2. 有未结束任务：`args.Cancel = true`，用 `showingClose` 挡住重入（`:269-271`）。
3. 调用 `ForceCloseAsync`（`src/ImgZip.Core/MainViewModel.Tasks.cs:63-70` → `src/ImgZip.Core/TaskCoordinator.cs:494-539`）：
   - 在 `taskGate` 内取未结束任务快照，**先取消排队者的 `QueueCancellation`**，避免杀进程期间有任务从队列里溜出来启动（`:497-503`）。
   - 逐个 `TerminateAsync`（`:545-561`）。worker 实现了 `IWorkerTracker` 就走真杀路径，否则退化为优雅取消。
   - `WorkerClient.TerminateAsync`（`src/ImgZip.Core/WorkerClient.cs:85-103`）：`Kill(entireProcessTree: true)` → 最多等 5 秒 → 用 `HasExited` 复核。**`Kill` 返回不代表进程已消失**，所以必须等确认（`WorkerClient.cs:92-94`）。`entireProcessTree` 一并带走 caesiumclt。
   - 杀成功：清暂存目录（`:525`）→ 写 `finished` / `cancelled` 终态并删记录（`:526-536`）。
   - 杀失败：状态置 `unknown`，记录保留并写回 `running` 阶段（`:514-522`），继续处理下一个任务。
4. `allowClose = true` → `Attachment.Dispose()` → `Close()`（`MainWindow.xaml.cs:275-277`）。

开发机上该路径实测约 25 ms（进程树已退出）。**来源为开发记录中的单次观测，未做重复采样，不作为性能承诺。**

## 恢复时的三分支判定

重启时对每条未结束记录判定一次，分支在 `ResolveProcessIdentity` 与 `RecoverTasksAsync` 内（`src/ImgZip.Core/TaskCoordinator.cs:217-238,253-273`）：

| 记录里的进程身份 | `IsAlive` | 代码位置 | 结果 | 占名额 | 记录 |
| --- | --- | --- | --- | --- | --- |
| 有 PID（记录字段或 `worker.pid`） | true | `:219,236` | `running`，提示「上一会话的执行进程仍在运行，正在接管」 | 是 | 保留 |
| 有 PID | false | `:220,238` | `FinalizeInterruptedAsync` 写 `cancelled` 终态 | 否 | 写终态后删除 |
| 没有 PID（0.4.0 之前的旧记录，或 `worker.pid` 读不到） | 无法判定 | `:216-233` | `unknown`，提示「旧记录未包含进程信息，无法自动判定」 | **是** | 保留 |

补充两条：

- `queued` 记录不参与上述判定，恢复为 `awaitingResume`，不占名额（`:216,227-229`）。
- 已确认终态的 `finished` 记录在恢复时直接收尾并删除，不再重现成「待检查」（`:207-214`）。

第三行是**有意保守**：没有 PID 就无法证明进程已死，宁可占着名额等用户处理，也不放一个可能还在跑的进程继续写输出。第一行接管的进程**不会**被自动补杀——重启不是终止时机，用户没要求停就一直跑；再点 X 时按记录里的 PID 终止（见「终止路径」）。

## 残留清理

worker 的 per-batch 清理在 `finally` 里（`worker/imgzip-worker.ps1:447-453`）。进程被 `Kill` 后 `finally` 不会执行，输出目录里会留下 `.imgzip-<jobId>-bN`，内含写了一半的引擎产物。

`StagingCleaner.Clean(outputDirectory, jobId)`（`src/ImgZip.Core/ForceClose.cs:24-44`）在杀成功之后调用（`TaskCoordinator.cs:525`）。它按前缀 + 指定 jobId 枚举，再逐项复核目录名（`ForceClose.cs:36-38`），确保只删本任务的暂存目录，不碰同目录下别的任务的产物。删不掉的路径收进 `ForceCloseReport.CleanupErrors`。

`CleanAll`（`ForceClose.cs:50-65`）是为「应用崩溃、来不及走强制关闭」准备的启动兜底，**当前没有调用点**。

## 为什么新开 `IWorkerTracker` 而不改 `IWorkerClient`

`IWorkerClient` 只有一个 `ExecuteAsync` 方法（`src/ImgZip.Core/WorkerClient.cs:7-10`）。既有测试替身 `ControlledWorker`、`FakeWorker` 都只实现它。若把 `TerminateAsync` 与 `ProcessStarted` 直接加进去，**所有替身都要改**，而这批替身正是回归测试的基线。

所以拆成独立接口（`src/ImgZip.Core/IWorkerTracker.cs:7-16`），由运行时类型判断决定走哪条路：

```csharp
// TaskCoordinator.cs:26 —— 只有真实现才订阅
if (worker is IWorkerTracker tracker) tracker.ProcessStarted += RememberProcess;
// TaskCoordinator.cs:547-556 —— 没有终止能力时退化为发取消标志 + 最多等 10 秒
```

真实 `WorkerClient` 同时实现两个接口（`WorkerClient.cs:12`）。替身不实现 `IWorkerTracker`，`TerminateAsync` 就退化为「发取消标志并等待任务自己收尾」，返回值仍是 `task.TerminalConfirmed`——**宁可返回 false，也不谎称已停止**。

效果：原有 47 组用例一行未改，新增 7 组后 54 组全过；测试文件只有新增、没有删除（`tests/ImgZip.Tests/LifecycleTests.cs`，+186 行）。`FakeWorker` 与 `ControlledWorker` 完全没动。

## 失败路径

| 情形 | 行为 | 用户可见结果 |
| --- | --- | --- |
| `Kill` 抛「拒绝访问」 | `TerminateAsync` 返回 false（`WorkerClient.cs:97`） | 记录保留 `unknown`，下次启动按 PID 定论 |
| 5 秒内未确认退出 | 返回 false（`WorkerClient.cs:93`） | 同上 |
| 任务无本进程句柄，但记录里 PID 有效 | 按 PID 接管终止：`GetProcessById` → 校验启动时刻（2 秒容差）→ 杀进程树 | 确认退出则删记录；PID 已被别的进程占用则视为已停止 |
| 终态写盘失败 | `FinalizeTaskAsync` 捕获并把原因追加到 `Message`（`TaskCoordinator.cs:432-443`） | 记录可能残留，下次按 `finished` 恢复 |
| 暂存清理失败 | 计入 `CleanupErrors` | **当前不展示**，没有 UI 读这个字段 |
| `ForceCloseAsync` 抛异常 | `Message = "关闭失败：…"`，窗口保留（`MainWindow.xaml.cs:279`） | 窗口不关，提示可见 |

这里有一个容易被忽略的断点：**「杀不掉」不算异常**，窗口照常关闭（`MainWindow.xaml.cs:274-277`）。`MainViewModel` 里给 `Message` 赋的「N 个任务未能确认停止」提示（`MainViewModel.Tasks.cs:67-68`）会随窗口一起消失，用户实际看不到。真正兜底的是下次启动的 PID 判定。只有 `ForceCloseAsync` 本身抛异常时窗口才保留。

## 改动清单

| 文件 | 改动 |
| --- | --- |
| `src/ImgZip.Core/IWorkerTracker.cs`（新） | 可终止句柄：`ProcessStarted` 事件 + `TerminateAsync` |
| `src/ImgZip.Core/IProcessProbe.cs`（新） | `IsAlive(pid, startedAt)` |
| `src/ImgZip.Core/SystemProcessProbe.cs`（新） | `Process` 实现，2 秒启动时刻容差 |
| `src/ImgZip.Core/ForceClose.cs`（新） | `ForceCloseReport` + `StagingCleaner` |
| `src/ImgZip.Core/WorkerClient.cs` | 实现 `IWorkerTracker`；登记在跑进程；`TerminateAsync` 杀进程树 |
| `src/ImgZip.Core/TaskStore.cs` | `TaskRecord` 加 `WorkerPid` / `WorkerStartedAt` |
| `src/ImgZip.Core/TaskCoordinator.cs` | `RememberProcess`、`ResolveProcessIdentity`、`FinalizeInterruptedAsync`、`ForceCloseAsync`、`TerminateAsync`、三分支恢复 |
| `src/ImgZip.Core/CompressionTaskViewModel.cs` | 任务级 `WorkerPid` / `WorkerStartedAt` |
| `src/ImgZip.Core/MainViewModel.Tasks.cs` | `ForceCloseAsync` 门面 |
| `src/ImgZip.App/MainWindow.xaml.cs` | 删除三选对话框，改直接强制关闭 |
| `worker/imgzip-worker.ps1` | run 分支开头写 `jobs/<jobId>/worker.pid` |
| `tests/ImgZip.Tests/LifecycleTests.cs` | 新增 6 组，无删除 |

## 已测与未测

已测（`tests/ImgZip.Tests` 控制台检查，54 组通过；原 47 组零修改，新增 7 组）：

- 强制关闭确认终止：`report.Terminated == 1`、任务池清空、记录文件删除（`LifecycleTests.cs:387-405`）
- **接管进程按记录 PID 终止**：`TerminatedPids` 收到记录里的 PID 12345（`:406-430`）
- 强制关闭未确认：记录保留 `running` + PID、状态 `unknown`、仍占名额（`:366-386`）
- 恢复三分支各一组：进程已死→中断收尾（`:303-321`）、进程存活→接管并占名额（`:322-345`）、无 PID→保持 `unknown`（`:346-365`）
- 暂存清理只删本 job、不误删他人（`:282-302`）

未测：

- **真实 Windows 上 `Process.Kill(entireProcessTree)` 杀 caesiumclt**。已用真实的 PowerShell 进程树验证（worker 起子进程，按 PID 终止后两者均消失，27 ms），但没有在真实压缩任务跑到一半时终止、也没有核对 caesiumclt 是否留有临时文件。
- **真实 NAS 任务的强制关闭**（远端 `setsid` 进程，见限制 1）。
- 安装包与新版本 UI 的手工验收。

## 已知限制

1. **NAS 远端任务不受本地强杀控制。** 本地杀的是 PowerShell 与 ssh；远端 runner 由 `setsid` 拉起（`worker/nas-worker.sh:109`），ssh 断开不会带走它。此时本地记录已写 `cancelled`，远端可能仍在压缩。`worker.pid` 记的也是本地 PowerShell 的 PID，不是远端进程。
2. ~~重启后接管的进程杀不掉~~ **已修复。** `IWorkerTracker.TerminateAsync` 现在接收 `(jobId, processId, startedAt)`：先查本会话句柄，没有则按记录里的 PID 接管终止，且校验启动时刻以排除 PID 复用。用真进程树实测：新 `WorkerClient` 实例无任何句柄，按 PID 终止耗时 27 ms，worker 与子进程均消失。
3. **暂存清理依赖 `OutputPath` 已知。** `StagingCleaner` 用 `task.OutputPath` 当根目录（`TaskCoordinator.cs:525`）；任务还没收到过带 `output` 的事件时它是占位文本「等待开始」（`src/ImgZip.Core/CompressionTaskViewModel.cs:51`），`Clean` 静默返回，暂存目录留到下次。
4. ~~`processes` 字典只写不读~~ **已删除。** PID 直接存在任务的 `WorkerPid` / `WorkerStartedAt` 上，不再维护平行字典。
5. **`jobs/<jobId>` 目录不随记录删除。** `TaskStore.DeleteAsync` 只删 `tasks/<jobId>.json`（`src/ImgZip.Core/TaskStore.cs:59-65`），`worker.pid` 与 journal 一直留着。恢复判定依赖它，代价是目录会累积。
6. **没有 PID 的记录不会自动消失。** 只有用户点「重新检查」才可能离开 `unknown`。来源是两类：0.4.0 之前的旧记录，以及记录已写 `launching`、进程尚未启动这个极窄窗口内崩溃的新记录（此时 `worker.pid` 也还没写）。
7. **未做**启动时 `CleanAll` 调用，也**未做**把 `CleanupErrors` 显示给用户。
