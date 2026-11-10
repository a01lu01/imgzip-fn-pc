namespace ImgZip.Core;

/// <summary>
/// 执行层进程的可终止句柄。由 <see cref="WorkerClient"/> 实现；
/// 测试替身不实现该接口时，调用方必须退化为“写取消标志”的优雅取消路径。
/// </summary>
public interface IWorkerTracker
{
    /// <summary>执行层进程启动后同步触发，携带 jobId、进程 ID 与启动时刻。</summary>
    event Action<string, int, DateTimeOffset>? ProcessStarted;

    /// <summary>
    /// 杀掉该任务的执行层进程树并等待确认。
    /// <paramref name="processId"/> 用于覆盖本进程没有句柄的情况——重启后接管的任务只有
    /// 记录里的 PID，没有 <see cref="System.Diagnostics.Process"/> 实例，仍必须能终止。
    /// 返回 false 表示无法确认已停止，调用方不得据此宣称任务已取消。
    /// </summary>
    Task<bool> TerminateAsync(string jobId, int processId, DateTimeOffset startedAt);
}
