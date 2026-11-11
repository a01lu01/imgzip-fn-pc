# 任务目录回收：停止写事件日志

**状态：已实现，0.4.0 开发线。** 57 组检查通过。未提交发版。

## 背景

`%LOCALAPPDATA%\ImgZip\jobs\<jobId>\` 是每个任务的运行期目录，用来放两样东西：取消标志 `cancel`，以及 0.4.0 加的 `worker.pid` 崩溃恢复兜底。

它还有第三样东西：`events.jsonl`。worker 把每一行发往 stdout 的 JSON 事件同时追加到这个文件，供 `inspect` 回放找出终态。

问题出在没人删它。`TaskStore.DeleteAsync` 只删 `tasks/<jobId>.json`，`jobs/` 一直留着。到 2026-11-11 为止累积了 **118 个目录、3.99 MB**，从 9 月 13 日起无一清理，平均每个任务 34.7 KB、最大 94 KB。

## 判断：这份日志没有存在价值

减体积本身不足以构成删除理由，4 MB 在今天不算什么。真正的理由是它证明不了任何事。

`inspect` 的语义是「这个任务还在跑吗」。日志能回答的是「上次写到哪」，而**日志的最后一行不等于进程的当前状态**——进程被杀在两次写入之间、断电、文件写入缓冲未落盘，都会让日志停在中间。所以旧 `inspect` 的结论永远是「读到 finished 就信，否则 unknown」，后者其实什么也没说。

0.4.0 的强制关闭已经把「这个进程还在不在」变成了可直接查询的事实：PID 加启动时刻，`worker.pid` 就落在同一个目录里。同一个问题，一份日志给出推测，一个 PID 给出答案。

于是保留日志只剩下累积成本。

## 改动

### worker：停止写日志

`worker/imgzip-worker.ps1` 删掉 `$journal`、`$recordEvents` 两个变量和 `Emit` 里的 `AppendAllText` 那一行。stdout 协议一字未改，逐行 JSON 照旧。

### worker：`inspect` 改按进程事实判定

`operation=inspect` 的 PC 分支重写。读 `jobs/<jobId>/worker.pid` 得到 `pid` 与 `startedAt`，然后：

| 条件 | 结果 | 依据 |
| --- | --- | --- |
| `worker.pid` 不存在 | `unknown` | 没有可判定的对象 |
| 解析失败或 `pid <= 0` | `unknown` | 记录损坏 |
| 进程存活，启动时刻在 2 秒内 | `unknown` | 还在跑，不能宣称结束 |
| 进程不在，**且** `cancel` 标志存在 | **`cancelled`** | 用户请求过停止，且进程确实没了 |
| 进程不在，无 `cancel` 标志 | `unknown` | 进程没了，但不知道是怎么结束的 |

只有第四行是**主张**，其余都是**观察**。这是全表最重要的一格：`cancelled` 需要「请求」加「事实」两个条件同时成立，缺任何一个都退回 `unknown`。

启动时刻比较沿用应用侧的 2 秒容差（`SystemProcessProbe`），用来排除 PID 复用。`startedAt` 为空时只按 PID 判活并接受该风险。

### 应用：任务收尾即回收

新增 `src/ImgZip.Core/JobDirectories.cs`：

- `Clean(stateDirectory, jobId)` —— 删单个目录。在 `FinalizeTaskAsync` 删掉记录之后调用（`src/ImgZip.Core/TaskCoordinator.cs:443-446`）。此刻 `cancel` 与 `worker.pid` 都不再有读者。
- `Sweep(stateDirectory)` —— 启动兜底。删除 `jobs/` 下所有**没有对应 `tasks/<jobId>.json`** 的目录，在 `RecoverTasksAsync` 末尾调用。

`Sweep` 的判据是磁盘而不是内存：恢复跑完之后，`tasks/` 里剩下的正好就是仍活着的任务。所以历史遗留、异常退出残留、旧版本攒下的目录会被一并收掉，不需要额外的过期时间或数量上限。

目录名在拼接前校验为 32 位小写十六进制。非法的名字一律不碰。

### 接口：`ITaskStore.DirectoryPath`

`JobDirectories` 需要知道用户数据目录，但 `TaskCoordinator` 只持有 `ITaskStore`。加了默认接口实现：

```csharp
string? DirectoryPath => null;
```

返回 `null` 时清理静默跳过。用默认实现而不是抽象成员，是为了让 `FaultStore` 这类测试替身不必改动——零测试修改的约束到阶段 2 才到期。

## 什么必须保留

**未确认终止的任务，目录不能删。**

`window_Closing` 杀不掉进程时，记录保留、状态 `unknown`，下次启动要靠 `proc` 事实定论。而那个 `worker.pid` 就是唯一的证据。删了它，一个已知的 `unknown` 就变成彻底的猜测。

代码上这条由路径保证：未确认分支 `continue`（`TaskCoordinator.cs:530`），不会走到 `FinalizeTaskAsync`；`Sweep` 也因为它仍有 `tasks/<jobId>.json` 而跳过。

这条有专门的测试守着：`job directory survives an unconfirmed force close` 断言 `worker.pid` 在强制关闭后仍然存在。

## worker 侧的判定细节

`inspect` 的存活判断在 Windows PowerShell 5.1 上有个坑：`Get-Process -Id 4` 这类受保护进程拿到的对象，`HasExited` 返回空值、`StartTime` 抛异常。若按「取到对象且 `HasExited` 为假 → 活着」写，这类进程会被漏判。

实现改成**只有三个证据之一才判死**：取不到进程、`HasExited` 为真、启动时刻差超过 2 秒。`HasExited` 与 `StartTime` 都用 try 包住，任何读取失败一律按**活着**处理。宁可多报一次 `unknown`，也不在没有证据时宣称终态。

## 测试

新增 3 组，`tests/ImgZip.Tests/LifecycleTests.cs:430-488`：

| 测试 | 断言 |
| --- | --- |
| `job directory is removed when its task finishes` | 本任务目录消失；**无关目录保留**（防误删） |
| `job directory survives an unconfirmed force close` | `Unconfirmed == 1` 且 `worker.pid` 仍在 |
| `startup sweep removes job directories without records` | 返回 1；孤儿目录删、有记录目录留、非法 ID 目录留 |

变异验证两轮：把 `Clean` 改成直接返回，测试 1 报 `Finished task left its job directory behind`；把 `IsSafeId` 改成恒真，测试 3 报 `Startup sweep removed 2 job directories instead of 1`。第二轮抓到的是真实缺陷——初版 `Sweep` 确实会删非法名字的目录。

## 已测与未测

已测：

- 真 worker + 真引擎跑完整任务：`events.jsonl` 不再产生，运行期目录为空，任务收尾后目录消失
- 预置孤儿目录在恢复时被 `Sweep` 清掉
- 57 组检查全过，原有 54 组零修改

未测：

- 真实 NAS 任务的 `inspect`（该分支未改动）
- 生产环境那 118 个历史目录的实际清理效果（逻辑对得上，但没有在真实用户数据上跑过）

## 已知限制

1. **`inspect` 的证据强度下降了。** 旧实现至少在日志里有终态时能读到具体计数；现在只能给出三选一的结论。这换来的是「结论可证明」，代价是「结论更粗」。若将来需要精确的失败详情，应当写进 `tasks/<jobId>.json` 而不是另开日志。
2. **PNG 分支的 `inspect` 依赖 `worker.pid` 的写入成功。** 该文件写入是 try/catch 静默的（权限或磁盘问题会失败）。此时 `inspect` 返回 `unknown`，与「找不到记录」同义。
3. **`Sweep` 在每次恢复时全量扫描 `jobs/`。** 当前目录数在几十的量级，成本可忽略；若将来单用户积累到数万级，需要改成按 `createdAt` 索引。
4. **未做** `CleanupErrors` 的 UI 展示，也**未做**启动时 `StagingCleaner.CleanAll` 调用（那是输出目录的 `.imgzip-*` 残留，与本目录无关）。
