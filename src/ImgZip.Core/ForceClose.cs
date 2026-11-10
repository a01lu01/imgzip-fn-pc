namespace ImgZip.Core;

/// <summary>
/// 强制关闭的结果。<see cref="Terminated"/> 是已确认停止并写好终态的任务数；
/// <see cref="Unconfirmed"/> 是杀不掉、记录保留并以 PID 标注待下次定论的任务数。
/// </summary>
public sealed record ForceCloseReport(int Terminated, int Unconfirmed, IReadOnlyList<string> CleanupErrors)
{
    public bool FullyStopped => Unconfirmed == 0;
}

/// <summary>
/// 清理 worker 被强杀后遗留的暂存目录。worker 的 finally 块不会执行，
/// 所以输出目录里会留下 .imgzip-&lt;jobId&gt;-bN，内含写了一半的引擎产物。
/// </summary>
public static class StagingCleaner
{
    private const string Prefix = ".imgzip-";

    /// <summary>
    /// 删除指定 jobId 在 <paramref name="outputDirectory"/> 下的全部暂存目录。
    /// 目录不存在或没有匹配项时静默返回。返回无法删除的路径。
    /// </summary>
    public static IReadOnlyList<string> Clean(string? outputDirectory, string jobId)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory)) return failures;
        if (string.IsNullOrWhiteSpace(jobId)) return failures;
        var pattern = Prefix + jobId + "-*";
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateDirectories(outputDirectory, pattern, SearchOption.TopDirectoryOnly).ToArray(); }
        catch (IOException) { return failures; }
        catch (UnauthorizedAccessException) { return failures; }
        foreach (var candidate in candidates)
        {
            // 只删我们自己的暂存目录：前缀 + 指定 jobId，避免误删别的任务的产物。
            var name = Path.GetFileName(candidate);
            if (!name.StartsWith(pattern.TrimEnd('*'), StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(candidate, recursive: true); }
            catch (IOException) { failures.Add(candidate); }
            catch (UnauthorizedAccessException) { failures.Add(candidate); }
        }
        return failures;
    }

    /// <summary>
    /// 启动时兜底：清理 <paramref name="outputDirectory"/> 下所有 <c>.imgzip-</c> 暂存目录。
    /// 用于覆盖应用崩溃、来不及走强制关闭流程的情况。
    /// </summary>
    public static IReadOnlyList<string> CleanAll(string? outputDirectory)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory)) return failures;
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateDirectories(outputDirectory, Prefix + "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch (IOException) { return failures; }
        catch (UnauthorizedAccessException) { return failures; }
        foreach (var candidate in candidates)
        {
            try { Directory.Delete(candidate, recursive: true); }
            catch (IOException) { failures.Add(candidate); }
            catch (UnauthorizedAccessException) { failures.Add(candidate); }
        }
        return failures;
    }
}
