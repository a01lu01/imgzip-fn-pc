using System.Diagnostics;
using System.Text.Json;
using ImgZip.Core;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var scratch = Path.Combine(root, "artifacts", "tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
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
    Check(imported.Port == 22 && imported.Theme == "system", "Legacy defaults changed");
    var store = new ConfigStore(Path.Combine(scratch, "config"));
    await store.SaveAsync(imported with { Theme = "dark" });
    Check((await store.LoadAsync()).Theme == "dark", "Theme not persisted");
    Check(await File.ReadAllTextAsync(legacy) == original, "Import modified the original");
    Check(!Directory.EnumerateFiles(store.DirectoryPath, "*.tmp").Any(), "Temporary configuration leaked");
    Check(Wire.Decode<AppConfig>("""{"theme":"invalid"}""").Normalize().Theme == "system", "Invalid theme not normalized");
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
await Test("unknown task locks inputs; inspection recovers; confirmed terminal is sticky", async () =>
{
    var mock = new FakeWorker();
    var store = new ConfigStore(Path.Combine(scratch, "vm"));
    var vm = new MainViewModel(mock, store, "unused");
    await vm.InitializeAsync();
    var path = Path.Combine(scratch, "vm-image.jpg"); await File.WriteAllTextAsync(path, "image");
    await vm.SetSourcesAsync([path]);
    await vm.StartAsync();
    Check(vm.IsUnknown && !vm.CanEdit && File.Exists(Path.Combine(store.DirectoryPath, "active-job.json")), "Unknown task was not protected");
    vm.ClearSources(); Check(vm.SourcePath == path, "Locked source was changed");
    await vm.InspectAsync(); Check(vm.State == "succeeded" && vm.CanEdit, "Inspection did not recover");
    Check(!File.Exists(Path.Combine(store.DirectoryPath, "active-job.json")), "Confirmed task marker retained");
    vm.Apply(new WorkerEvent { JobId = mock.JobId, Type = "finished", State = "unknown" });
    Check(vm.State == "succeeded", "Late unknown replaced confirmed success");
    mock.PauseRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    var running = vm.StartAsync();
    Check(vm.IsLocked, "Start did not lock inputs before its first await");
    await vm.SetSourcesAsync([Path.Combine(scratch, "replacement.jpg")]);
    Check(vm.SourcePath == path, "Preparing job accepted another source");
    await vm.StartAsync();
    mock.PauseRun.SetResult(); await running;
});

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

sealed class FakeWorker : IWorkerClient
{
    public string JobId { get; private set; } = "";
    public TaskCompletionSource? PauseRun { get; set; }
    public async Task ExecuteAsync(WorkerRequest request, Action<WorkerEvent> onEvent)
    {
        if (request.Operation == "probe") onEvent(new() { JobId = request.JobId, Type = "probe", PcAvailable = true });
        else
        {
            JobId = request.JobId;
            if (request.Operation == "run" && PauseRun is not null) await PauseRun.Task;
            onEvent(new() { JobId = request.JobId, Type = "finished", State = request.Operation == "inspect" ? "succeeded" : "unknown" });
        }
    }
}
