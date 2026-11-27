# 测试怎么写

仓库**没有 xUnit / NUnit**。检查是普通控制台程序，靠抛异常表示失败。这样做的原因是：大部分被测代码要在真实的 `SynchronizationContext` 下跑异步生命周期，还要启动真实的 PowerShell 子进程，用测试框架反而要对抗它的并行与隔离模型。

---

## 1. 怎么跑

```powershell
pwsh -NoProfile -File build/test.ps1
```

依次执行：

| 步骤 | 内容 |
| --- | --- |
| 1 | 构建 `tests/ImgZip.FakeEngine`（锁定模式还原） |
| 2 | `dotnet run tests/ImgZip.Tests` —— C# 检查，当前 **74 组** |
| 3 | `tests/nas-worker-concurrency.sh` —— Git Bash，4 组 |
| 4 | `tests/nas-worker-copy.sh` —— Git Bash |
| 5 | `worker/imgzip-worker.ps1` 语法解析 |

第 3、4 步找不到 `bash.exe` 时打印 `SKIP` 而不是失败。第 5 步只解析不执行。

测试证据写在 `artifacts/tests/<GUID>/`（已 gitignore），末尾会打印路径。

---

## 2. 结构

```
tests/ImgZip.Tests/Program.cs          入口 + 大部分检查 + FakeWorker + SerialTestContext
tests/ImgZip.Tests/LifecycleTests.cs   任务生命周期专项（916 行）+ 4 个测试替身
tests/ImgZip.Tests/RecallTests.cs      纯函数决策的断言
tests/ImgZip.FakeEngine/Program.cs     替身引擎，兼作 SSH 传输层替身
```

### 入口长这样

```csharp
using var ui = new SerialTestContext();
SynchronizationContext.SetSynchronizationContext(ui);
var testRun = RunTestsAsync();
ui.Pump(testRun);          // 关键：边跑边把投递到 sync context 的续体抽干
testRun.GetAwaiter().GetResult();
```

**`SerialTestContext` 的作用**：`TaskCoordinator` 通过 `Dispatch` 把回调送回 UI 线程。测试里没有消息循环，如果不把这个 `SynchronizationContext` 装上去并手动泵，任务永远停在「已投递未执行」，表现为随机超时。它不是测试洁癖，是必需品。

### 加一条检查

在 `RunTestsAsync()` 里任意位置：

```csharp
await Test("一句话说明被破坏时会怎样", async () =>
{
    Check(condition, "失败时打印的这句话要说清哪条不变量被破坏");
});
```

`Test` 负责计数并打印 `PASS <名字>`；`Check` 抛异常即失败。

专项测试放在单独文件里，用同一个 `Test` 回调接入：

```csharp
// Program.cs
await RecallTests.RunAsync(Test);
await LifecycleTests.RunAsync(scratch, Test);
```

`LifecycleTests` 里还有一个 `Model(name, failPhase, dispatch)` 辅助方法，一行建出「配置 + 故障存储 + 受控执行层 + ViewModel」四件套，多窗口场景再传 `coordinator` 共享名额池。

---

## 3. 测试替身

都在 `tests/` 内，**没有一处被 `internal` 暴露到生产代码**——生产侧全是接口。

| 替身 | 位置 | 干什么 | 什么时候用 |
| --- | --- | --- | --- |
| `FakeWorker` | `Program.cs:546` | `IWorkerClient`。可按脚本产出事件，能统计并发峰值（`MaxActiveRuns` / `MaxActiveNas`）、能挂起（`HoldRuns` / `ReleaseRuns`）、能伪装 NAS 核数与内存 | 验证名额分配、并发上限、排队顺序 |
| `SerialTestContext` | `Program.cs:595` | 把 `Post` 排进队列，`Pump` 循环抽干直到任务完成 | 所有涉及 `Dispatch` 的检查 |
| `ControlledWorker` | `LifecycleTests.cs:836` | 每次 `run` 挂起，由测试调 `Finish(jobId, state)` 决定结局 | 需要精确控制「任务何时结束」的生命周期检查 |
| `FaultStore` | `LifecycleTests.cs:857` | 包装真的 `TaskStore`，可让指定 phase 写盘失败（`FailPhase`）、让 `launching` 卡住、让删除失败 | 验证写盘失败时的行为，而非写盘本身 |
| `FixedProbe` | `LifecycleTests.cs:878` | `IProcessProbe`，死活由构造函数决定 | 验证进程存活判定分支，不依赖真实 PID |
| `StoppableWorker` | `LifecycleTests.cs:883` | 能报告进程身份、能记录终止请求（含 PID）的 `IWorkerClient` | 强制关闭与崩溃恢复 |
| `ImgZip.FakeEngine` | `tests/ImgZip.FakeEngine` | 见下 | 不装图片编解码器也能跑真实 worker |

### FakeEngine 有两种人格

1. **假引擎**：被 `imgzip-worker.ps1` 当 `caesiumclt.exe` 调用时，按参数校验（缺 `-e` 返回 13、`--lossless` 与 `-q` 同用返回 14 等），把输入文件截半写到输出目录。文件名前缀是约定：`slow*` 睡 20 秒、`fail*` 报解码失败、`empty*` 报输出为空。这样真实 worker 的组批、配对字节、部分失败逻辑全都能测，且结果确定。
2. **假 SSH**：当参数里同时有 `bash` 和 `-s` 时，它不执行收到的 Bash，而是从 `printf '%s' '<base64>' | base64 -d` 里解出变量，按 `MODE` 回放预设事件。主机名后缀决定人格：`@authfail` 报认证失败、`@offline` 报不可达、`@missing` 表示连上了但没有引擎、`disconnect` 源路径触发传输中断。

**加新事件字段时**，`FakeEngine` 与 `Models.cs` 要一起改，否则字段被反序列化成默认值而不报错。

---

## 4. 变异验证

这是本仓库对「检查真的有效」的定义：**把实现改坏，看检查是否变红；改回来，看是否恢复绿。**

只写一条通过的检查不算数——它可能永远为真。步骤：

1. 记下文件哈希。
2. 把实现改成**用户报告的那个错误行为**（不是随便改坏，是改回出问题时的写法）。
3. **刷新时间戳**：`(Get-Item <file>).LastWriteTime = (Get-Date).AddMinutes(2)`。不刷的话 MSBuild 可能跳过编译，你会看到「改坏了还是绿的」，然后误以为检查无效，或者反过来以为修复生效。
4. 跑检查，记录**报错的那句话**。
5. 还原文件，再次刷新时间戳，确认哈希一致且检查恢复绿。

如果第一条命中的断言不是你想验证的那条（检查按文件顺序执行，先跑的会先失败），临时放宽那条以看清后面那条的报错——**确认后必须恢复**，并在提交说明里写清楚放宽过。

历史用法举例见 [UI-ISSUES-0.5.0-followup.md](UI-ISSUES-0.5.0-followup.md) 问题 2 的变异表：三条变异分别对应「问题 2 原样」「问题 1 原样」「冻结条件缺失」，每条都记录了具体报错文案。

---

## 5. 检查覆盖不到的地方

诚实清单——以下**没有**自动化覆盖，只能人工或脚本化实机验证：

| 不能测的 | 原因 | 替代手段 |
| --- | --- | --- |
| XAML 编译与渲染 | 需要 Windows + XamlCompiler | `build/windows.ps1` 真机构建 |
| 窗口可见性、只读、按钮增删 | 需要真实窗口 | UI Automation 读控件树 + 截图 |
| 任务栏按钮行为 | Win11 任务栏无 UIA 子元素 | 截图按颜色定位 + 注入鼠标事件 |
| 前台窗口归属 | 需要真实前台权 | 见 [AGENTS.md](../AGENTS.md) 4.3 |
| 真实 caesium 编码质量 | 需要真实引擎与图片 | 手动跑 demo-images |
| 真实 NAS 连接 | 需要设备 | 手动；`FakeEngine` 的假 SSH 只覆盖协议 |

`build/test.ps1` 末尾那句 `No visual or interactive browser/Windows validation performed` 就是提醒这件事——**检查全绿不等于界面已验证**。
