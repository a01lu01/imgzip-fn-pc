using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImgZip.Core;

public sealed record TaskRecord
{
    public int SchemaVersion { get; init; } = 2;
    public WorkerRequest Request { get; init; } = new();
    public string Phase { get; init; } = "queued";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public WorkerEvent? Result { get; init; }
    public string[] Failures { get; init; } = [];
    /// <summary>执行层进程 ID；0 表示尚未启动或未记录。重启时用它判断任务是否真的还在跑。</summary>
    public int WorkerPid { get; init; }
    /// <summary>执行层进程的启动时刻（UTC）。与 PID 一起排除 PID 复用。</summary>
    public DateTimeOffset WorkerStartedAt { get; init; }
}

public sealed record TaskRecovery(IReadOnlyList<TaskRecord> Records, IReadOnlyList<string> Errors);

public interface ITaskStore
{
    Task<TaskRecovery> LoadAsync();
    Task SaveAsync(TaskRecord record);
    Task DeleteAsync(string jobId);
    /// <summary>
    /// 记录所在的用户数据目录。用于定位同级的 <c>jobs/</c>。
    /// 默认实现返回 null，测试替身不必实现；返回 null 时相关清理静默跳过。
    /// </summary>
    string? DirectoryPath => null;
}

public sealed class TaskStore(string directory) : ITaskStore
{
    // 共享协调者后，多个窗口会并发写同一批记录；ConfigStore 有同样的 gate，这里此前缺失。
    // LoadAsync 内部会调用 SaveCoreAsync，故用 reentrant 标志避免自锁。
    private readonly SemaphoreSlim gate = new(1, 1);
    public string DirectoryPath => directory;
    private string TasksPath => Path.Combine(directory, "tasks");
    private string RecordPath(string id)
    {
        if (!Regex.IsMatch(id, "^[a-f0-9]{32}$")) throw new InvalidDataException("任务 ID 无效。");
        return Path.Combine(TasksPath, id + ".json");
    }
    public async Task SaveAsync(TaskRecord record) => await SaveCoreAsync(record, reentrant: false);
    private async Task SaveCoreAsync(TaskRecord record, bool reentrant)
    {
        Validate(record);
        var path = RecordPath(record.Request.JobId);
        Directory.CreateDirectory(TasksPath);
        if (!reentrant) await gate.WaitAsync();
        try
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, Wire.Encode(record));
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { if (!reentrant) gate.Release(); }
    }
    public async Task DeleteAsync(string jobId)
    {
        var file = RecordPath(jobId);
        await gate.WaitAsync();
        try { if (File.Exists(file)) File.Delete(file); }
        finally { gate.Release(); }
    }
    private static void Validate(TaskRecord record)
    {
        var request = record.Request;
        if (record.SchemaVersion != 2 || request is null || request.Version != 1
            || request.JobId is null || !Regex.IsMatch(request.JobId, "^[a-f0-9]{32}$")
            || request.Engine is not ("pc" or "nas") || request.Sources is not { Length: > 0 }
            || request.Sources.Any(string.IsNullOrWhiteSpace)
            || request.Options is null || request.Config is null || record.Failures is null
            || record.WorkerPid < 0
            || record.Phase is not ("queued" or "launching" or "running" or "finished"))
            throw new InvalidDataException("任务记录版本或内容无效。");
        if (record.Phase == "finished")
        {
            if (record.Result is null || record.Result.Type != "finished" || record.Result.State == "unknown")
                throw new InvalidDataException("任务记录缺少可信终态。");
            WorkerEvent.Parse(Wire.Encode(record.Result), request.JobId);
        }
    }
    private static TaskRecord Decode(string text, DateTimeOffset createdAt)
    {
        using var document = JsonDocument.Parse(text);
        var record = document.RootElement.TryGetProperty("schemaVersion", out _)
            ? Wire.Decode<TaskRecord>(text)
            // Legacy requests cannot prove that their worker never started.
            : new TaskRecord { Request = Wire.Decode<WorkerRequest>(text), Phase = "launching", CreatedAt = createdAt };
        Validate(record);
        return record;
    }
    public async Task<TaskRecovery> LoadAsync()
    {
        var records = new Dictionary<string, TaskRecord>(StringComparer.Ordinal);
        var errors = new List<string>();
        await gate.WaitAsync();
        try { return await LoadCoreAsync(records, errors); }
        finally { gate.Release(); }
    }
    private async Task<TaskRecovery> LoadCoreAsync(Dictionary<string, TaskRecord> records, List<string> errors)
    {
        var legacyMarker = Path.Combine(directory, "active-job.json");
        var files = Directory.Exists(TasksPath) ? Directory.GetFiles(TasksPath, "*.json").Order().ToList() : [];
        if (File.Exists(legacyMarker)) files.Add(legacyMarker);
        foreach (var file in files)
        {
            try
            {
                var text = await File.ReadAllTextAsync(file);
                var record = Decode(text, File.GetLastWriteTimeUtc(file));
                var id = record.Request.JobId;
                if (file != legacyMarker && file != RecordPath(id)) throw new InvalidDataException("任务文件名与 ID 不匹配。");
                if (records.TryGetValue(id, out var existing)) record = existing;
                else records[id] = record;
                // Register a valid unresolved job even if migration cannot be saved; it still holds capacity.
                await SaveCoreAsync(record, reentrant: true);
                if (file == legacyMarker) File.Delete(file);
            }
            catch (Exception ex) { errors.Add($"任务记录无法恢复或迁移，原记录已保留：{file}\n{ex.Message}"); }
        }
        return new(records.Values.OrderBy(r => r.CreatedAt).ToArray(), errors);
    }
}
