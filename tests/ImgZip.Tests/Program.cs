using System.Diagnostics;
using System.Text.Json;
using ImgZip.Core;

using var ui = new SerialTestContext();
SynchronizationContext.SetSynchronizationContext(ui);
var testRun = RunTestsAsync();
ui.Pump(testRun);
testRun.GetAwaiter().GetResult();

async Task RunTestsAsync()
{
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var scratch = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
async Task WaitUntilAsync(Func<bool> condition, string message, int timeoutMs = 10000)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline) throw new TimeoutException(message);
        await Task.Delay(20);
    }
}
async Task Test(string name, Func<Task> test)
{
    await test(); checks++; Console.WriteLine($"PASS {name}");
}
await Test("config import, defaults, atomic save and theme persistence", async () =>
{
    var legacy = Path.Combine(scratch, "legacy.json");
    var original = """{"server":"192.168.1.10","user":"nas","keyPath":"C:\\密钥\\id_nas","nasHosts":["NAS"]}""";
    await File.WriteAllTextAsync(legacy, original);
    var imported = await ConfigStore.ImportAsync(legacy);
    Check(imported.Port == 22 && imported.Theme == "system" && imported.NasConcurrency == 2, "Legacy defaults changed");
    Check(new AppConfig { NasConcurrency = 0 }.Normalize().NasConcurrency == 2 && new AppConfig { NasConcurrency = 9 }.Normalize().NasConcurrency == 2, "NAS concurrency normalization failed");
    var store = new ConfigStore(Path.Combine(scratch, "config"));
    await store.SaveAsync(imported with { Theme = "dark", NasConcurrency = 3 });
    var persisted = await store.LoadAsync();
    Check(persisted.Theme == "dark" && persisted.NasConcurrency == 3, "Theme or NAS concurrency not persisted");
    Check(await File.ReadAllTextAsync(legacy) == original, "Import modified the original");
    Check(!Directory.EnumerateFiles(store.DirectoryPath, "*.tmp").Any(), "Temporary configuration leaked");
    Check(Wire.Decode<AppConfig>("""{"theme":"invalid"}""").Normalize().Theme == "system", "Invalid theme not normalized");
});
await Test("engine mode defaults to auto for legacy configs and normalizes invalid values", async () =>
{
    // 0.4.0 阶段 0：本次只引入字段与兜底，不改 SetSourcesAsync 的引擎判定逻辑。
    // 旧配置缺字段时必须等价于 auto，升级零行为变化。
    var store = new ConfigStore(Path.Combine(scratch, "engine-mode"));
    Directory.CreateDirectory(store.DirectoryPath);
    await File.WriteAllTextAsync(store.FilePath, """{"server":"192.168.1.10","user":"nas","keyPath":"C:\\key\\id_nas"}""");
    var legacy = await store.LoadAsync();
    Check(legacy.EngineMode == "auto", $"Legacy config should default to auto, got '{legacy.EngineMode}'");
    Check(legacy.LastEngine == "pc", $"Legacy config should default LastEngine to pc, got '{legacy.LastEngine}'");

    var defaults = new AppConfig();
    Check(defaults.EngineMode == "auto" && defaults.LastEngine == "pc", "AppConfig defaults changed");

    Check(Wire.Decode<AppConfig>("""{"engineMode":"bogus"}""").Normalize().EngineMode == "auto", "Invalid engine mode not normalized");
    Check(Wire.Decode<AppConfig>("""{"lastEngine":"mac"}""").Normalize().LastEngine == "pc", "Invalid last engine not normalized");
    Check(Wire.Decode<AppConfig>("""{"engineMode":null,"lastEngine":null}""").Normalize() is { EngineMode: "auto", LastEngine: "pc" }, "Null engine fields not normalized");
    Check(new AppConfig { EngineMode = "last", LastEngine = "nas" }.Normalize() is { EngineMode: "last", LastEngine: "nas" }, "Valid engine mode or last engine was dropped");
    Check(new AppConfig { EngineMode = "fixedPc" }.Normalize().EngineMode == "fixedPc", "fixedPc engine mode was dropped");

    await store.SaveAsync(new AppConfig { EngineMode = "last", LastEngine = "nas" });
    var persisted = await store.LoadAsync();
    Check(persisted is { EngineMode: "last", LastEngine: "nas" }, "Engine mode or last engine not persisted");
});
await Test("copy skipped files preference persists in config", async () =>
{
    var store = new ConfigStore(Path.Combine(scratch, "copy-preference"));
    Directory.CreateDirectory(store.DirectoryPath);
    await File.WriteAllTextAsync(store.FilePath, """{"copySkippedFiles":true}""");
    var loaded = await store.LoadAsync();
    var property = typeof(AppConfig).GetProperty("CopySkippedFiles");
    Check(property is not null, "CopySkippedFiles config property is missing");
    Check((bool)property!.GetValue(loaded)!, "CopySkippedFiles preference was not loaded");
    await store.SaveAsync(loaded);
    Check((bool)property.GetValue(await store.LoadAsync())!, "CopySkippedFiles preference was not persisted");
});
await Test("presets, parameter exclusivity and validation", () =>
{
    var custom = new CompressionOptions { Lossless = true, MaxSize = 100, NoUpscale = false };
    var preset = CompressionOptions.ForPreset("webp-share", custom);
    Check(preset.Quality == 80 && preset.Pixels == 2560 && preset.Format == "webp" && !preset.Lossless && preset.MaxSize == 0 && preset.NoUpscale, "Preset mismatch");
    Check((custom with { Quality = -1, MaxSize = -1 }).Validate() is null, "Disabled options should not be used");
    Check((preset with { Threads = 0 }).Validate() is not null, "Invalid threads accepted");
    Check((preset with { Quality = 101 }).Validate() is not null, "Invalid quality accepted");
    Check((preset with { Resize = "none", Pixels = 0 }).Validate() is null, "No-resize rejected");
    Check((preset with { Format = "unknown" }).Validate() is not null, "Unknown format accepted");
    return Task.CompletedTask;
});
await Test("protocol rejects wrong identity, version, counters and malformed JSON", () =>
{
    var id = Guid.NewGuid().ToString("N");
    foreach (var invalid in new[] { "bad-json", Wire.Encode(new WorkerEvent { JobId = "wrong", Type = "phase" }), Wire.Encode(new WorkerEvent { Version = 2, JobId = id, Type = "phase" }), Wire.Encode(new WorkerEvent { JobId = id, Type = "file", Total = 1, Completed = 2 }) })
    {
        var rejected = false;
        try { WorkerEvent.Parse(invalid, id); } catch (Exception ex) when (ex is JsonException or InvalidDataException) { rejected = true; }
        Check(rejected, "Bad protocol accepted");
    }
    var valid = WorkerEvent.Parse(Wire.Encode(new WorkerEvent { JobId = id, Type = "finished", State = "partial", Total = 3, Completed = 3, Succeeded = 2, Failed = 1, BeforeBytes = 100, AfterBytes = 120 }), id);
    Check(Display.Summary(valid).Contains("增加"), "Larger output reported as savings");
    return Task.CompletedTask;
});
await Test("unknown task retained; draft stays editable; inspection recovers", async () =>
{
    var mock = new FakeWorker();
    var store = new ConfigStore(Path.Combine(scratch, "vm"));
    var vm = new MainViewModel(mock, store, "unused");
    await vm.InitializeAsync();
    var path = Path.Combine(scratch, "vm-image.jpg"); await File.WriteAllTextAsync(path, "image");
    await vm.SetSourcesAsync([path]);
    await vm.StartAsync();
    Check(vm.IsUnknown && vm.CurrentTask?.IsUnknown == true, "Unknown task was not retained");
    Check(Directory.GetFiles(Path.Combine(store.DirectoryPath, "tasks"), "*.json").Length == 1, "Unresolved task record missing");
    Check(vm.CanEdit, "Draft controls should remain enabled");
    vm.ClearSources(); Check(vm.SourcePath != path, "Draft should stay editable while a task is unresolved");
    await vm.InspectAsync(); Check(vm.State == "succeeded" && vm.CurrentTask is { IsUnknown: false }, "Inspection did not recover");
    Check(!File.Exists(Path.Combine(store.DirectoryPath, "active-job.json")), "Confirmed task marker retained");
    var jobId = mock.JobId;
    vm.Apply(new WorkerEvent { JobId = jobId, Type = "finished", State = "unknown" });
    Check(vm.State == "succeeded", "Late unknown replaced confirmed success");
    mock.PauseRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    await vm.SetSourcesAsync([path]);
    var running = vm.StartAsync();
    await vm.SetSourcesAsync([Path.Combine(scratch, "replacement.jpg")]);
    Check(vm.SourcePath.EndsWith("replacement.jpg"), "Draft should accept new sources while a task runs");
    mock.PauseRun.SetResult(); await running;
});

await Test("PC concurrency respects configured limit and queues the rest", async () =>
{
    var mock = new FakeWorker { HoldRuns = true, RunState = "succeeded" };
    var store = new ConfigStore(Path.Combine(scratch, "mt"));
    await store.SaveAsync(new AppConfig { PcConcurrency = 2 });
    var first = new MainViewModel(mock, store, "unused");
    await first.InitializeAsync();
    var windows = new List<MainViewModel> { first };
    for (var i = 1; i < 3; i++)
    {
        var window = new MainViewModel(mock, store, "unused", null, null, first.Coordinator);
        await window.InitializeAsync();
        windows.Add(window);
    }
    var runs = new List<Task>();
    for (var i = 0; i < 3; i++)
    {
        var source = Path.Combine(scratch, $"mt{i}.jpg"); await File.WriteAllTextAsync(source, "image");
        await windows[i].SetSourcesAsync([source]);
        runs.Add(windows[i].StartAsync());
    }
    await WaitUntilAsync(() => mock.MaxActiveRuns == 2 && windows.Count(w => w.CurrentTask?.IsQueued == true) == 1,
        $"PC concurrency limit not reached: active={mock.ActiveRuns}, max={mock.MaxActiveRuns}");
    Check(mock.MaxActiveRuns == 2, $"PC concurrency limit not respected: {mock.MaxActiveRuns}");
    Check(windows.Count(w => w.CurrentTask?.IsQueued == true) == 1, "Excess PC task should stay queued");
    mock.ReleaseRuns();
    await Task.WhenAll(runs);
});

await Test("NAS tasks serialize; PC tasks may run alongside", async () =>
{
    var mock = new FakeWorker { HoldRuns = true, RunState = "succeeded" };
    var store = new ConfigStore(Path.Combine(scratch, "mt-nas"));
    await store.SaveAsync(new AppConfig { PcConcurrency = 2 });
    var disk = new TaskStore(store.DirectoryPath);
    foreach (var source in new[] { @"\\nas.local\photos\a.jpg", @"\\nas.local\photos\b.jpg" })
        await disk.SaveAsync(new TaskRecord { Request = new() { Engine = "nas", Sources = [source], StateDirectory = store.DirectoryPath } });
    var vm = new MainViewModel(mock, store, "unused");
    await vm.InitializeAsync();
    Check(vm.Coordinator.Tasks.All(t => t.CanResume), "Recovered queue must wait for user");
    var runs = vm.Coordinator.Tasks.ToArray().Select(vm.ResumeTaskAsync).ToList();
    var pc = new MainViewModel(mock, store, "unused", null, disk, vm.Coordinator);
    await pc.InitializeAsync();
    await pc.SetSourcesAsync([Path.Combine(scratch, "alongside.jpg")]);
    runs.Add(pc.StartAsync());
    await WaitUntilAsync(() => mock.MaxActiveNas == 1 && mock.ActiveRuns == 2,
        $"PC/NAS concurrency not reached: maxNas={mock.MaxActiveNas}, active={mock.ActiveRuns}, requests={mock.RunRequests.Count}");
    Check(mock.MaxActiveNas == 1 && mock.RunRequests.Count(r => r.Engine == "nas") == 1, "NAS test must start exactly one task before release");
    Check(mock.ActiveRuns == 2, "PC should run alongside NAS");
    mock.ReleaseRuns();
    await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));
    Check(mock.RunRequests.Count(r => r.Engine == "nas") == 2, "Both NAS requests must actually execute");
});

await Test("NAS engine concurrency comes from NAS setting and detected core cap", async () =>
{
    var mock = new FakeWorker { HoldRuns = true, RunState = "succeeded", NasCores = 2, NasMemoryMb = 2048 };
    var store = new ConfigStore(Path.Combine(scratch, "nas-limit"));
    await store.SaveAsync(new AppConfig { PcConcurrency = 3, NasConcurrency = 4, Server = "nas.local", User = "nas", KeyPath = "C:\\key", NasHosts = ["nas.local"] });
    var vm = new MainViewModel(mock, store, "unused");
    await vm.InitializeAsync();
    await vm.SetSourcesAsync([@"\\nas.local\photos\shot.jpg"]);
    Check(vm.EffectiveNasConcurrency == 2, $"NAS setting not clamped to detected cores: {vm.EffectiveNasConcurrency}");
    var run = vm.StartAsync();
    await WaitUntilAsync(() => mock.RunRequests.Count == 1, "NAS task did not start");
    Check(mock.RunRequests[0].Options.Threads == 2 && mock.RunRequests[0].Options.Threads != Environment.ProcessorCount,
        $"NAS task inherited PC concurrency: {mock.RunRequests[0].Options.Threads}");
    mock.ReleaseRuns();
    await run;
});

await Test("NAS concurrency setting does not change PC task thread allocation", async () =>
{
    var mock = new FakeWorker { HoldRuns = true, RunState = "succeeded" };
    var store = new ConfigStore(Path.Combine(scratch, "pc-independent"));
    await store.SaveAsync(new AppConfig { PcConcurrency = 2, NasConcurrency = 1 });
    var first = new MainViewModel(mock, store, "unused");
    await first.InitializeAsync();
    var second = new MainViewModel(mock, store, "unused", null, null, first.Coordinator);
    await second.InitializeAsync();
    var windows = new[] { first, second };
    var runs = new List<Task>();
    for (var i = 0; i < 2; i++)
    {
        var source = Path.Combine(scratch, $"pc-independent-{i}.jpg");
        await File.WriteAllTextAsync(source, "image");
        await windows[i].SetSourcesAsync([source]);
        runs.Add(windows[i].StartAsync());
    }
    await WaitUntilAsync(() => mock.RunRequests.Count == 2, "Both PC tasks did not start");
    var expectedFirst = Math.Max(1, Environment.ProcessorCount);
    var expectedSecond = Math.Max(1, Environment.ProcessorCount / 2);
    var threads = mock.RunRequests.Select(r => r.Options.Threads).OrderByDescending(value => value).ToArray();
    Check(first.EffectivePcConcurrency == 2 && threads.SequenceEqual([expectedFirst, expectedSecond]),
        $"PC thread allocation changed with NAS setting: {string.Join(",", threads)}");
    mock.ReleaseRuns();
    await Task.WhenAll(runs);
});

await Test("copy preference applies to folders but not file sources", async () =>
{
    var mock = new FakeWorker { HoldRuns = true, RunState = "succeeded" };
    var store = new ConfigStore(Path.Combine(scratch, "copy-source"));
    await store.SaveAsync(new AppConfig { CopySkippedFiles = true });
    var vm = new MainViewModel(mock, store, "unused");
    await vm.InitializeAsync();
    var folder = Path.Combine(scratch, "copy-source-folder");
    Directory.CreateDirectory(folder);
    await vm.SetSourcesAsync([folder]);
    var folderRun = vm.StartAsync();
    await WaitUntilAsync(() => mock.RunRequests.Count == 1, "Folder task did not start");
    Check(mock.RunRequests[0].Options.CopySkippedFiles, "Folder task did not apply copy preference");
    mock.ReleaseRuns();
    await folderRun;

    var replacement = Path.Combine(scratch, "copy-source-image.jpg");
    await File.WriteAllTextAsync(replacement, "image");
    await vm.SetSourcesAsync([replacement]);
    var fileRun = vm.StartAsync();
    await WaitUntilAsync(() => mock.RunRequests.Count == 2, "File task did not start");
    Check(!mock.RunRequests[1].Options.CopySkippedFiles, "File task incorrectly enabled folder-only copy option");
    mock.ReleaseRuns();
    await fileRun;
});

await Test("saving connection settings preserves copy preference", async () =>
{
    var mock = new FakeWorker();
    var store = new ConfigStore(Path.Combine(scratch, "copy-connection"));
    await store.SaveAsync(new AppConfig { CopySkippedFiles = true });
    var vm = new MainViewModel(mock, store, "unused");
    await vm.InitializeAsync();
    await vm.SaveConnectionAsync(new AppConfig { Server = "nas.local", User = "nas", KeyPath = "C:\\key", NasHosts = ["nas.local"] });
    Check((await store.LoadAsync()).CopySkippedFiles, "Saving connection settings reset copy preference");
});

await Test("copy events update task progress and failures", () =>
{
    var task = new CompressionTaskViewModel(new() { Engine = "pc", Sources = ["folder"] }, 0);
    task.Apply(new WorkerEvent { JobId = task.JobId, Type = "manifest", Total = 1, CopyTotal = 2 });
    task.Apply(new WorkerEvent { JobId = task.JobId, Type = "copy", State = "copied", CopyTotal = 2, CopyCompleted = 1, Copied = 1, CopiedBytes = 1024, Path = "a.mov" });
    var property = typeof(CompressionTaskViewModel).GetProperty("CopyProgressText");
    Check(property is not null, "CopyProgressText property is missing");
    Check(((string)property!.GetValue(task)!).Contains("1 / 2") && ((string)property.GetValue(task)!).Contains("1.0 KB"), "Copy progress text incorrect");
    task.Apply(new WorkerEvent { JobId = task.JobId, Type = "copy", State = "failed", CopyTotal = 2, CopyCompleted = 2, Copied = 1, CopyFailed = 1, CopiedBytes = 1024, Path = "b.mov", Message = "locked" });
    Check(task.HasFailures && task.Failures.Single().Contains("复制失败") && task.Failures.Single().Contains("locked"), "Copy failure was not surfaced");
    return Task.CompletedTask;
});
await Test("dry run summary reports planned copied files", () =>
{
    var summary = Display.Summary(new WorkerEvent { Type = "finished", State = "dryRun", DryRun = true, Total = 2, CopyTotal = 3, Skipped = 3 });
    Check(summary.Contains("其他文件 3"), summary);
    return Task.CompletedTask;
});

await Test("successful task record is cleaned up and removed from the list", async () =>
{
    var mock = new FakeWorker { RunState = "succeeded" };
    var store = new ConfigStore(Path.Combine(scratch, "cleanup"));
    var vm = new MainViewModel(mock, store, "unused");
    await vm.InitializeAsync();
    var source = Path.Combine(scratch, "cleanup.jpg"); await File.WriteAllTextAsync(source, "image");
    await vm.SetSourcesAsync([source]);
    await vm.StartAsync();
    Check(vm.Coordinator.Tasks.Count == 0 && vm.CurrentTask is { State: "succeeded" },
        "Successful task was not removed from the pool");
    Check(!Directory.EnumerateFiles(Path.Combine(store.DirectoryPath, "tasks"), "*.json").Any(), "Task record should be deleted");
});

await Test("engine picker honours mode and eligibility", () =>
{
    // 阶段 6：PickEngine 把「模式决定的候选」和「来源资格」分开，资格是硬约束。
    // auto
    Check(MainViewModel.PickEngine("auto", true, "pc") == 1, "auto should pick NAS for eligible source");
    Check(MainViewModel.PickEngine("auto", false, "nas") == 0, "auto should pick PC for local source");
    // fixedPc
    Check(MainViewModel.PickEngine("fixedPc", true, "nas") == 0, "fixedPc should ignore eligibility and preference");
    // last
    Check(MainViewModel.PickEngine("last", true, "nas") == 1, "last should reuse the remembered NAS");
    Check(MainViewModel.PickEngine("last", true, "pc") == 0, "last should reuse the remembered PC");
    Check(MainViewModel.PickEngine("last", false, "nas") == 0, "last should fall back when NAS is not eligible");
    // 未知模式按 auto 处理
    Check(MainViewModel.PickEngine("garbage", true, "pc") == 1, "unknown mode should behave like auto");
    Check(MainViewModel.PickEngine("garbage", false, "nas") == 0, "unknown mode should behave like auto");
    // eligibility 是硬约束：fixedPc 之外，任何模式下 NAS 不可达都不能选 NAS
    Check(MainViewModel.PickEngine("last", false, "pc") == 0, "ineligible source should never select NAS");
    return Task.CompletedTask;
});

await LifecycleTests.RunAsync(scratch, Test);

var shell = Environment.GetEnvironmentVariable("IMGZIP_TEST_PWSH") ?? (OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : Path.Combine(root, ".tools/pwsh/pwsh"));
if (!File.Exists(shell)) throw new Exception("PowerShell test runtime missing: " + shell);
var engineDirectory = Path.Combine(root, "tests/ImgZip.FakeEngine/bin/Release/net10.0");
string engine;
if (OperatingSystem.IsWindows()) engine = Path.Combine(engineDirectory, "ImgZip.FakeEngine.exe");
else
{
    engine = Path.Combine(scratch, "fake-engine");
    string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";
    await File.WriteAllTextAsync(engine, $"#!/bin/sh\nexec {Sh(Path.Combine(root, ".tools/dotnet/dotnet"))} {Sh(Path.Combine(engineDirectory, "ImgZip.FakeEngine.dll"))} \"$@\"\n");
    File.SetUnixFileMode(engine, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
var client = new WorkerClient(shell, Path.Combine(root, "worker/imgzip-worker.ps1"));
WorkerRequest Request(string[] paths, CompressionOptions? options = null) => new()
{
    Sources = paths, Options = options ?? new(), EnginePath = engine,
    StateDirectory = Path.Combine(scratch, "worker-state")
};
async Task<List<WorkerEvent>> Execute(WorkerRequest request)
{
    var events = new List<WorkerEvent>(); await client.ExecuteAsync(request, events.Add);
    return events;
}
async Task<string> Folder(string name, params string[] files)
{
    var directory = Path.Combine(scratch, name); Directory.CreateDirectory(directory);
    foreach (var file in files)
    {
        var path = Path.Combine(directory, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Enumerable.Repeat((byte)65, 100).ToArray());
    }
    return directory;
}
await Test("real PowerShell worker: dry run creates no output or engine result", async () =>
{
    var source = await Folder("dry", "good.jpg", "sub/nested.png", "note.txt");
    var events = await Execute(Request([source], new() { DryRun = true, Recurse = false }));
    var last = events.Last(); Check(last.State == "dryRun" && last.Total == 1 && last.Skipped == 1, Wire.Encode(last));
    Check(!Directory.Exists(source + "_compressed"), "Dry run created output");
    events = await Execute(Request([source], new() { DryRun = true, Recurse = true }));
    Check(events.Last().Total == 2, "Recursive manifest failed");
});
await Test("real PowerShell worker: copies skipped regular files after compression", async () =>
{
    var source = await Folder("copy-other", "good.jpg", "note.txt", "sub/nested.txt");
    var options = Wire.Decode<CompressionOptions>("""{"format":"jpeg","quality":90,"resize":"none","pixels":0,"copySkippedFiles":true}""");
    var events = await Execute(Request([source], options));
    var last = events.Last();
    Check(last.State == "succeeded", Wire.Encode(last));
    Check(File.Exists(Path.Combine(last.Output, "note.txt")), "Skipped file was not copied");
    Check(!File.Exists(Path.Combine(last.Output, "sub/nested.txt")), "Non-recursive copy entered a subdirectory");
});
await Test("real PowerShell worker: recursive copy preserves directories and reports progress", async () =>
{
    var source = await Folder("copy-recursive", "good.jpg", "note.txt", "sub/nested.txt");
    var events = await Execute(Request([source], new() { CopySkippedFiles = true, Recurse = true, Format = "jpeg", Resize = "none", Pixels = 0 }));
    var last = events.Last();
    var copies = events.Where(e => e.Type == "copy").ToArray();
    Check(last.State == "succeeded" && copies.Length == 2, Wire.Encode(last));
    Check(File.Exists(Path.Combine(last.Output, "note.txt")) && File.Exists(Path.Combine(last.Output, "sub/nested.txt")), "Recursive copied files missing");
    var total = typeof(WorkerEvent).GetProperty("CopyTotal");
    var completed = typeof(WorkerEvent).GetProperty("CopyCompleted");
    var copied = typeof(WorkerEvent).GetProperty("Copied");
    var failed = typeof(WorkerEvent).GetProperty("CopyFailed");
    Check(total is not null && completed is not null && copied is not null && failed is not null, "Copy progress protocol fields are missing");
    Check((int)total!.GetValue(last)! == 2 && (int)completed!.GetValue(last)! == 2 && (int)copied!.GetValue(last)! == 2 && (int)failed!.GetValue(last)! == 0, "Copy progress counters incorrect");
});
await Test("real PowerShell worker: copy-only folder preserves ordinary hidden files", async () =>
{
    var source = await Folder("copy-only", "Thumbs.db", ".hidden.txt", "video.mov");
    var events = await Execute(Request([source], new() { CopySkippedFiles = true }));
    var last = events.Last();
    Check(last.State == "succeeded" && last.Total == 0 && last.CopyTotal == 3 && last.Copied == 3, Wire.Encode(last));
    Check(File.Exists(Path.Combine(last.Output, "Thumbs.db")) && File.Exists(Path.Combine(last.Output, ".hidden.txt")) && File.Exists(Path.Combine(last.Output, "video.mov")), "Copy-only output incomplete");
});
await Test("real PowerShell worker: copy failure makes a partial archive", async () =>
{
    if (!OperatingSystem.IsWindows()) return;
    var source = await Folder("copy-fail", "good.jpg", "open.raw", "locked.raw");
    var openSource = Path.Combine(source, "open.raw");
    var expectedTime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    File.SetLastWriteTimeUtc(openSource, expectedTime);
    using var locked = new FileStream(Path.Combine(source, "locked.raw"), FileMode.Open, FileAccess.Read, FileShare.None);
    var events = await Execute(Request([source], new() { CopySkippedFiles = true }));
    var last = events.Last();
    Check(last.State == "partial" && last.CopyTotal == 2 && last.Copied == 1 && last.CopyFailed == 1, Wire.Encode(last));
    Check(File.Exists(Path.Combine(last.Output, "open.raw")), "Successful copy was removed");
    Check(File.GetLastWriteTimeUtc(Path.Combine(last.Output, "open.raw")) == expectedTime, "Copied timestamp was not preserved");
    Check(events.Any(e => e.Type == "copy" && e.State == "failed" && e.Path.EndsWith("locked.raw")), "Copy failure event missing");
});
await Test("real worker: Unicode, spaces, special characters and same-format handling", async () =>
{
    var special = OperatingSystem.IsWindows() ? "旅途 ' $ & 한글.jpg" : "旅途 ' \" $ & 한글.jpg";
    var source = await Folder("unicode", special);
    var events = await Execute(Request([source])); var last = events.Last();
    Check(last.State == "succeeded" && last.Succeeded == 1 && last.BeforeBytes == 100 && last.AfterBytes == 50, Wire.Encode(last));
    Check(new FileInfo(Path.Combine(source, special)).Length == 100, "Original image modified");
});
await Test("real worker: existing output, conversion collisions and partial failure accounting", async () =>
{
    var source = await Folder("mixed", "same.jpg", "SAME.png", "fail.jpg", "empty.jpg");
    Directory.CreateDirectory(source + "_compressed"); await File.WriteAllBytesAsync(Path.Combine(source + "_compressed", "old.jpg"), new byte[4000]);
    var events = await Execute(Request([source])); var last = events.Last();
    Check(last.State == "partial" && last.Succeeded == 2 && last.Failed == 2, Wire.Encode(last));
    Check(last.BeforeBytes == 200 && last.AfterBytes == 100, "Old files or failed sources counted");
    Check(last.Output.EndsWith("_compressed_2"), "Existing output reused");
    Check(Directory.GetFiles(last.Output).Length == 2, "Conversion collision overwrote output");
    Check(Directory.GetFiles(last.Output).Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Case-equivalent output names were not disambiguated");
    Check(!Directory.EnumerateDirectories(last.Output, ".imgzip-*").Any(), "Staging directory retained");
});
await Test("real worker: lossless omits incompatible arguments and recursion preserves directories", async () =>
{
    var source = await Folder("recursive", "one.png", "sub/two.webp");
    var events = await Execute(Request([source], new() { Format = "keep", Resize = "none", Pixels = 0, Lossless = true, Quality = -1, MaxSize = -1, Recurse = true }));
    var last = events.Last(); Check(last.State == "succeeded" && last.Succeeded == 2, Wire.Encode(last));
    Check(File.Exists(Path.Combine(last.Output, "sub/two.webp")), "Directory structure lost");
});
await Test("real worker: cancellation confirms child exit and preserves a durable terminal record", async () =>
{
    var source = await Folder("cancel", "slow.jpg");
    var request = Request([source]);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var events = new List<WorkerEvent>();
    var run = client.ExecuteAsync(request, e => { events.Add(e); if (e.Type == "manifest") started.TrySetResult(); });
    await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
    await Task.Delay(800);
    var cancellation = await Execute(request with { Operation = "cancel" });
    Check(cancellation.Any(e => e.Type == "cancelRequested"), "No cancellation request acknowledgement");
    await run.WaitAsync(TimeSpan.FromSeconds(15));
    Check(events.Last().State == "cancelled", Wire.Encode(events.Last()));
    var inspected = await Execute(request with { Operation = "inspect" });
    Check(inspected.Last().State == "cancelled", "Durable status lost");
    if (Directory.Exists(events.Last().Output)) Check(!Directory.EnumerateFiles(events.Last().Output, "*", SearchOption.AllDirectories).Any(), "Cancelled partial output published");
});
await Test("NAS transport fixture: escaped paths, output mapping and connection/engine distinction", async () =>
{
    var key = Path.Combine(scratch, "private-key-reference"); await File.WriteAllTextAsync(key, "fixture, not a key");
    var request = Request([@"\\NAS\photos\trip\旅途 ' $ & 한글.jpg"]) with
    {
        Engine = "nas", SshPath = engine,
        Config = new() { Server = "NAS", User = "nas", KeyPath = key, Port = 2222, NasHosts = ["NAS"] }
    };
    var events = await Execute(request); var last = events.Last();
    Check(last.State == "succeeded", Wire.Encode(last));
    Check(events.Single(e => e.Type == "file").Path == "/vol1/photo/trip/旅途 ' $ & 한글.jpg", "Remote path was corrupted");
    Check(last.OpenOutput == @"\\NAS\photos\trip_compressed", "NAS result was not mapped back to UNC: " + last.OpenOutput);
    var probe = await Execute(request with { Operation = "probe", Config = request.Config with { Server = "missing" } });
    Check(probe.Last().Connected && !probe.Last().NasAvailable, "Connection and engine readiness conflated");
});
await Test("NAS transport fixture: auth and unreachable errors are distinguished", async () =>
{
    var key = Path.Combine(scratch, "private-key-reference");
    var request = Request([@"\\NAS\photos\trip\probe.jpg"]) with
    {
        Engine = "nas", SshPath = engine,
        Config = new() { Server = "NAS", User = "nas", KeyPath = key, Port = 2222, NasHosts = ["NAS"] }
    };
    var auth = await Execute(request with { Operation = "probe", Config = request.Config with { Server = "authfail" } });
    Check(auth.Last().State == "failed" && auth.Last().Message.Contains("NAS 认证失败"), Wire.Encode(auth.Last()));
    var offline = await Execute(request with { Operation = "probe", Config = request.Config with { Server = "offline" } });
    Check(offline.Last().State == "failed" && offline.Last().Message.Contains("NAS 不可达"), Wire.Encode(offline.Last()));
});
await Test("NAS transport fixture: disconnect and unconfirmed cancel remain unknown; inspection recovers", async () =>
{
    var key = Path.Combine(scratch, "private-key-reference");
    var request = Request([@"\\NAS\photos\trip\disconnect.jpg"]) with
    {
        Engine = "nas", SshPath = engine,
        Config = new() { Server = "NAS", User = "nas", KeyPath = key, NasHosts = ["NAS"] }
    };
    var events = await Execute(request); Check(events.Last().State == "unknown", "Disconnect falsely completed the job");
    var cancelled = await Execute(request with { Operation = "cancel" }); Check(cancelled.Last().State == "unknown", "Unconfirmed cancel falsely acknowledged");
    var recovered = await Execute(request with { Operation = "inspect" }); Check(recovered.Last().State == "succeeded", "Inspection could not recover");
});
await Test("client detects malformed and interrupted worker streams", async () =>
{
    var script = Path.Combine(scratch, "bad-worker.ps1");
    await File.WriteAllTextAsync(script, "[Console]::In.ReadLine() > $null; [Console]::Out.WriteLine('not-json')");
    var broken = new WorkerClient(shell, script);
    var rejected = false;
    try { await broken.ExecuteAsync(Request([]), _ => { }); } catch (InvalidDataException) { rejected = true; }
    Check(rejected, "Malformed stream accepted");
    await File.WriteAllTextAsync(script, "[Console]::In.ReadLine() > $null; exit 0");
    rejected = false;
    try { await broken.ExecuteAsync(Request([]), _ => { }); } catch (IOException) { rejected = true; }
    Check(rejected, "Silent exit accepted as completion");
    await File.WriteAllTextAsync(script, "$r=[Console]::In.ReadLine() | ConvertFrom-Json; @{version=1;jobId=$r.jobId;type='finished';state='succeeded'} | ConvertTo-Json -Compress; [Console]::Out.WriteLine('not-json')");
    var receivedFinal = false; rejected = false;
    try { await broken.ExecuteAsync(Request([]), e => receivedFinal |= e.Type == "finished"); } catch (InvalidDataException) { rejected = true; }
    Check(rejected && !receivedFinal, "Success delivered before the entire stream was validated");
});
Console.WriteLine($"\n{checks} checks passed. No visual or interactive browser/Windows validation performed.");
Console.WriteLine("Evidence: " + scratch);

}

sealed class FakeWorker : IWorkerClient
{
    public string JobId { get; private set; } = "";
    public TaskCompletionSource? PauseRun { get; set; }
    public string RunState { get; set; } = "unknown";
    public bool HoldRuns { get; set; }
    public List<WorkerRequest> RunRequests { get; } = [];
    public int ActiveRuns;
    public int MaxActiveRuns;
    public int ActiveNas;
    public int MaxActiveNas;
    public int NasCores { get; set; } = 4;
    public long NasMemoryMb { get; set; } = 4096;
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void ReleaseRuns() => release.TrySetResult();
    public async Task ExecuteAsync(WorkerRequest request, Action<WorkerEvent> onEvent)
    {
        if (request.Operation == "probe") { onEvent(new() { JobId = request.JobId, Type = "probe", PcAvailable = true, NasAvailable = true, Connected = true, NasCores = NasCores, NasMemoryMb = NasMemoryMb }); return; }
        JobId = request.JobId;
        if (request.Operation == "cancel") { onEvent(new() { JobId = request.JobId, Type = "finished", State = "cancelled" }); return; }
        if (request.Operation == "inspect") { onEvent(new() { JobId = request.JobId, Type = "finished", State = "succeeded" }); return; }
        var tracked = request.Operation == "run";
        if (tracked)
        {
            RunRequests.Add(request);
            Interlocked.Increment(ref ActiveRuns);
            MaxActiveRuns = Math.Max(MaxActiveRuns, ActiveRuns);
            if (request.Engine == "nas") { Interlocked.Increment(ref ActiveNas); MaxActiveNas = Math.Max(MaxActiveNas, ActiveNas); }
            onEvent(new() { JobId = request.JobId, Type = "manifest", State = "ready", Total = 1, Completed = 0 });
        }
        try
        {
            if (tracked && PauseRun is not null) await PauseRun.Task;
            if (tracked && HoldRuns) await release.Task;
            var state = tracked ? RunState : "unknown";
            if (tracked) onEvent(new() { JobId = request.JobId, Type = "file", State = state == "failed" ? "failed" : "succeeded", Index = 0, Total = 1, Completed = 1, Succeeded = state == "failed" ? 0 : 1, Failed = state == "failed" ? 1 : 0 });
            onEvent(new() { JobId = request.JobId, Type = "finished", State = state, Total = tracked ? 1 : 0, Completed = tracked ? 1 : 0, Succeeded = tracked && state != "failed" ? 1 : 0, Failed = tracked && state == "failed" ? 1 : 0 });
        }
        finally
        {
            if (tracked)
            {
                Interlocked.Decrement(ref ActiveRuns);
                if (request.Engine == "nas") Interlocked.Decrement(ref ActiveNas);
            }
        }
    }
}

sealed class SerialTestContext : SynchronizationContext, IDisposable
{
    private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => queue.Add((callback, state));
    public void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Test run timed out");
            if (queue.TryTake(out var item, 100)) item.Callback(item.State);
        }
    }
    public void Dispose() => queue.Dispose();
}
