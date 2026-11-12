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
    /// 崩溃兜底：清理某个任务的来源在输出目录里留下的暂存目录。
    /// 只在恢复流程里对「进程已确认死亡」的记录调用——那种情况下这些目录确定没有读者。
    ///
    /// 为什么不提供「扫描整个目录删掉所有 .imgzip-*」的版本：暂存目录活在输出目录里，
    /// 而另一个仍在跑的任务可能正往同一个输出目录写。前缀里只有 jobId 能区分归属，
    /// 丢掉 jobId 的批量删除会把别人的活干掉。
    /// </summary>
    public static IReadOnlyList<string> SweepSources(IEnumerable<string> sources, string jobId)
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(jobId)) return failures;
        // worker 的输出目录是「来源根目录 + _compressed」，已存在时加 _2、_3 后缀。
        var root = SourceRoot(sources);
        if (root is null) return failures;
        var parent = Path.GetDirectoryName(root);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent)) return failures;
        var prefix = Path.GetFileName(root) + "_compressed";
        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(parent, prefix + "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (IOException) { return failures; }
        catch (UnauthorizedAccessException) { return failures; }
        foreach (var candidate in candidates)
        {
            // 只认 _compressed、_compressed_2 这种形态，避免把「_compressed-backup」也算进来。
            var name = Path.GetFileName(candidate);
            var suffix = name[prefix.Length..];
            if (suffix.Length > 0 && !(suffix[0] == '_' && suffix[1..].All(char.IsAsciiDigit))) continue;
            failures.AddRange(Clean(candidate, jobId));
        }
        return failures;
    }

    /// <summary>与 worker 一致地取「来源根目录」：文件夹则取自身，文件则取所在目录。</summary>
    private static string? SourceRoot(IEnumerable<string> sources)
    {
        var first = sources.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return null;
        var trimmed = first.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (Directory.Exists(trimmed)) return trimmed;
        var parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrWhiteSpace(parent) ? null : parent;
    }
}
