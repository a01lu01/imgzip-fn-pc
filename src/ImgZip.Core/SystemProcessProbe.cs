using System.Diagnostics;
using System.Globalization;

namespace ImgZip.Core;

/// <summary>
/// 基于 <see cref="Process"/> 的实现。启动时刻允许少量容差：不同 API 返回的精度
/// 与本地时区换算会带来亚秒级偏差，而过大的容差又会削弱 PID 复用防护。
/// </summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(2);

    public bool IsAlive(int processId, DateTimeOffset startedAt)
    {
        if (processId <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            // worker 在受限环境读不到自身 StartTime 时会留空：此时无法排除 PID 复用，只能按 PID 判活。
            if (startedAt == default) return true;
            // StartTime 抛异常说明进程在取句柄之后退出了，按已停止处理。
            var actual = new DateTimeOffset(process.StartTime);
            if (actual.Offset != startedAt.Offset) actual = actual.ToOffset(startedAt.Offset);
            return (actual - startedAt).Duration() <= Tolerance;
        }
        catch (ArgumentException) { return false; }          // 没有该 PID
        catch (InvalidOperationException) { return false; }  // 进程已退出
        catch (System.ComponentModel.Win32Exception) { return false; } // 非本用户进程，不属于我们的任务
        catch (NotSupportedException) { return false; }      // 远端进程不支持 StartTime
    }

    /// <summary>用于诊断：把进程启动时刻格式化为可存储的字符串。</summary>
    public static string Format(DateTimeOffset startedAt) =>
        startedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
