namespace ImgZip.Core;

/// <summary>
/// 任务目录 <c>jobs/&lt;jobId&gt;</c> 的回收。
/// 它只服务于任务执行期与崩溃恢复：运行中放 <c>cancel</c> 标志，崩溃后留 <c>worker.pid</c> 作存活证据。
/// 任务一旦收尾，这些内容就再没有读者，留着只会无限累积。
/// </summary>
public static class JobDirectories
{
    private static string JobsPath(string stateDirectory) => Path.Combine(stateDirectory, "jobs");
    private static string TasksPath(string stateDirectory) => Path.Combine(stateDirectory, "tasks");

    /// <summary>
    /// 删除单个任务的目录。任务收尾后调用；未确认终止的任务**不得**调用，
    /// <c>worker.pid</c> 是下次启动判定死活的最后证据。
    /// 目录不存在时静默返回。删除失败不抛出：残留目录不影响正确性。
    /// </summary>
    public static void Clean(string? stateDirectory, string jobId)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory) || string.IsNullOrWhiteSpace(jobId)) return;
        // jobId 会被拼进路径，先确认它是纯十六进制，避免任何穿越。
        if (!IsSafeId(jobId)) return;
        try
        {
            var directory = Path.Combine(JobsPath(stateDirectory), jobId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 启动兜底：删除所有**没有对应任务记录**的任务目录。
    /// 判据是磁盘事实而不是内存状态——此刻 <c>tasks/</c> 里的记录恰好就是仍活着的任务，
    /// 所以运行中断后遗留的目录、以及历史版本攒下的目录都会被收掉。
    /// 返回删除的目录数。
    /// </summary>
    public static int Sweep(string? stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory)) return 0;
        var jobs = JobsPath(stateDirectory);
        if (!Directory.Exists(jobs)) return 0;
        IEnumerable<string> candidates;
        try { candidates = Directory.EnumerateDirectories(jobs, "*", SearchOption.TopDirectoryOnly).ToArray(); }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
        var removed = 0;
        foreach (var candidate in candidates)
        {
            var jobId = Path.GetFileName(candidate);
            if (!IsSafeId(jobId)) continue;
            // 有记录就保留：可能是另一个会话仍在跑的任务，也可能是等待定论的未确认任务。
            if (File.Exists(Path.Combine(TasksPath(stateDirectory), jobId + ".json"))) continue;
            try { Directory.Delete(candidate, recursive: true); removed++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static bool IsSafeId(string jobId) =>
        jobId.Length == 32 && jobId.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
