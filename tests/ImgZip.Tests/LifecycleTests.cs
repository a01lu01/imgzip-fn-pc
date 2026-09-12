using ImgZip.Core;

static class LifecycleTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Lifecycle fixture timed out");
    }
    public static async Task RunAsync(string scratch, Func<string, Func<Task>, Task> test)
    {
        async Task<(MainViewModel Vm, ControlledWorker Worker, ConfigStore Config, FaultStore Disk)> Model(string name, string? failPhase = null, Action<Action>? dispatch = null)
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", name));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new FaultStore(new TaskStore(config.DirectoryPath)) { FailPhase = failPhase };
            var worker = new ControlledWorker();
            var vm = new MainViewModel(worker, config, "fixture", dispatch, disk);
            await vm.InitializeAsync(); return (vm, worker, config, disk);
        }
        async Task<Task> Start(MainViewModel vm, string name)
        {
            await vm.SetSourcesAsync([Path.Combine(scratch, name + ".jpg")]);
            return vm.StartAsync();
        }
        await test("removed queued task never calls the execution layer", async () =>
        {
            var (vm, worker, _, _) = await Model("remove");
            var first = await Start(vm, "remove-first"); await Until(() => worker.Runs.Count == 1);
            var second = await Start(vm, "remove-second"); await Until(() => vm.Tasks.Any(t => t.IsQueued));
            var removed = vm.Tasks.Single(t => t.IsQueued);
            await vm.CancelTaskAsync(removed);
            worker.Finish(worker.Runs[0].JobId, "succeeded");
            await Task.WhenAll(first, second);
            Check(worker.Runs.Count == 1 && vm.Tasks.Count == 0, "Removed waiter launched or was retained");
        });
        await test("cancellation wins during durable launch reservation", async () =>
        {
            var (vm, worker, _, disk) = await Model("reserve-race");
            disk.BlockLaunching = true;
            var run = await Start(vm, "race");
            await disk.LaunchEntered.Task;
            var cancel = vm.CancelTaskAsync(vm.Tasks.Single());
            disk.AllowLaunch.TrySetResult();
            await Task.WhenAll(run, cancel);
            Check(worker.Runs.Count == 0 && vm.Tasks.Count == 0, "Task launched after cancellation during reservation");
        });
        foreach (var engine in new[] { "pc", "nas" })
        {
            await test($"{engine} unknown retains capacity until inspected terminal", async () =>
            {
                var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "unknown-" + engine));
                await config.SaveAsync(new() { PcConcurrency = 1 });
                var disk = new TaskStore(config.DirectoryPath);
                for (var i = 0; i < 2; i++)
                    await disk.SaveAsync(new() { Request = new() { Engine = engine, Sources = [engine == "nas" ? $@"\\nas\photos\{i}.jpg" : Path.Combine(scratch, $"unknown{i}.jpg")], StateDirectory = config.DirectoryPath }, CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i) });
                var worker = new ControlledWorker();
                var vm = new MainViewModel(worker, config, "fixture"); await vm.InitializeAsync();
                var tasks = vm.Tasks.ToArray();
                Check(worker.Runs.Count == 0 && tasks.All(t => t.CanResume), "Restored queue auto-started");
                var first = vm.ResumeTaskAsync(tasks[0]); await Until(() => worker.Runs.Count == 1);
                worker.Finish(tasks[0].JobId, "unknown"); await first;
                var second = vm.ResumeTaskAsync(tasks[1]); await Task.Delay(350);
                Check(worker.Runs.Count == 1 && tasks[1].IsQueued, "Unknown released its slot");
                await vm.InspectTaskAsync(tasks[0]); await Until(() => worker.Runs.Count == 2);
                worker.Finish(tasks[1].JobId, "succeeded"); await second;
                Check(!vm.HasUnfinishedTasks && vm.Tasks.Count == 0, "Confirmed terminal failed to release capacity");
                foreach (var phase in new[] { "launching", "running" })
                {
                    var unresolved = new WorkerRequest { Engine = engine, Sources = ["unresolved.jpg"], StateDirectory = config.DirectoryPath };
                    await disk.SaveAsync(new() { Request = unresolved, Phase = phase });
                    await disk.SaveAsync(new() { Request = unresolved with { JobId = Guid.NewGuid().ToString("N"), Sources = ["waiting.jpg"] } });
                    var restartedWorker = new ControlledWorker();
                    var restarted = new MainViewModel(restartedWorker, config, "fixture"); await restarted.InitializeAsync();
                    var waiter = restarted.Tasks.Single(t => t.CanResume);
                    var resumed = restarted.ResumeTaskAsync(waiter); await Task.Delay(350);
                    Check(restartedWorker.Runs.Count == 0 && waiter.IsQueued, $"Recovered {phase} failed to hold its slot");
                    await restarted.InspectTaskAsync(restarted.Tasks.Single(t => t.IsUnknown));
                    await Until(() => restartedWorker.Runs.Count == 1);
                    restartedWorker.Finish(waiter.JobId, "succeeded"); await resumed;
                    Check(!restarted.HasUnfinishedTasks, $"Recovered {phase} failed to release its slot");
                }
            });
        }
        await test("queued records restart paused; manual remove and resume work", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "paused"));
            var disk = new TaskStore(config.DirectoryPath);
            var records = Enumerable.Range(0, 2).Select(i => new TaskRecord { Request = new() { Sources = [Path.Combine(scratch, $"paused{i}.jpg")], StateDirectory = config.DirectoryPath } }).ToArray();
            foreach (var record in records) await disk.SaveAsync(record);
            var worker = new ControlledWorker(); var vm = new MainViewModel(worker, config, "fixture"); await vm.InitializeAsync();
            Check(worker.Runs.Count == 0 && vm.Tasks.All(t => t.CanResume && t.CanRemove), "Queue incorrectly restored");
            await vm.CancelTaskAsync(vm.Tasks.Single(t => t.JobId == records[0].Request.JobId));
            var remaining = vm.Tasks.Single(); var run = vm.ResumeTaskAsync(remaining);
            await Until(() => worker.Runs.Count == 1); worker.Finish(remaining.JobId, "succeeded"); await run;
            Check(!Directory.EnumerateFiles(Path.Combine(config.DirectoryPath, "tasks"), "*.json").Any(), "Manual queue actions left records");
        });
        await test("legacy marker and task records migrate once; corrupt records remain visible", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "migration"));
            var folder = Path.Combine(config.DirectoryPath, "tasks"); Directory.CreateDirectory(folder);
            var request = new WorkerRequest { Sources = [Path.Combine(scratch, "legacy.jpg")], StateDirectory = config.DirectoryPath };
            var marker = Path.Combine(config.DirectoryPath, "active-job.json");
            await File.WriteAllTextAsync(marker, Wire.Encode(request));
            await File.WriteAllTextAsync(Path.Combine(folder, request.JobId + ".json"), Wire.Encode(request));
            var bad = Path.Combine(folder, "broken.json"); await File.WriteAllTextAsync(bad, "broken-json");
            var incomplete = new TaskRecord { Request = request with { JobId = Guid.NewGuid().ToString("N") } };
            var incompletePath = Path.Combine(folder, incomplete.Request.JobId + ".json");
            var incompleteJson = Wire.Encode(incomplete).Replace("\"failures\":[]", "\"failures\":null");
            await File.WriteAllTextAsync(incompletePath, incompleteJson);
            var vm = new MainViewModel(new ControlledWorker(), config, "fixture"); await vm.InitializeAsync();
            Check(vm.Tasks.Count == 1 && vm.Tasks[0].IsUnknown && vm.Tasks[0].CanInspect, "Legacy task missing or duplicated");
            Check(!File.Exists(marker) && await File.ReadAllTextAsync(bad) == "broken-json", "Migration deleted unconverted data");
            Check(vm.Message.Contains("broken.json"), "Corrupt record error hidden");
            Check(vm.Message.Contains(incompletePath) && await File.ReadAllTextAsync(incompletePath) == incompleteJson, "Invalid record fields prevented recovery or were discarded");
            var record = Wire.Decode<TaskRecord>(await File.ReadAllTextAsync(Path.Combine(folder, request.JobId + ".json")));
            Check(record.SchemaVersion == 2 && record.Phase == "launching", "Legacy state guessed as not-started");
        });
        await test("standalone active-job marker becomes an inspectable task", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "marker-only")); Directory.CreateDirectory(config.DirectoryPath);
            var request = new WorkerRequest { Sources = ["legacy.jpg"], StateDirectory = config.DirectoryPath };
            await File.WriteAllTextAsync(Path.Combine(config.DirectoryPath, "active-job.json"), Wire.Encode(request));
            var vm = new MainViewModel(new ControlledWorker(), config, "fixture"); await vm.InitializeAsync();
            Check(vm.Tasks.Count == 1 && vm.CanInspect && vm.HasUnfinishedTasks, "Standalone marker not registered");
            await vm.InspectAsync(); Check(vm.Tasks.Count == 0, "Migrated task could not be finalized");
        });
        await test("migration write failure preserves old marker and registers unresolved task", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "migration-fail")); Directory.CreateDirectory(config.DirectoryPath);
            var request = new WorkerRequest { Sources = ["legacy.jpg"] };
            var marker = Path.Combine(config.DirectoryPath, "active-job.json"); await File.WriteAllTextAsync(marker, Wire.Encode(request));
            // A file blocks creation of the tasks directory, including under administrator test accounts.
            await File.WriteAllTextAsync(Path.Combine(config.DirectoryPath, "tasks"), "blocked");
            var vm = new MainViewModel(new ControlledWorker(), config, "fixture"); await vm.InitializeAsync();
            Check(vm.Tasks.Single().IsUnknown && File.Exists(marker) && vm.HasMessage, "Migration failed silently or lost legacy task");
        });
        foreach (var phase in new[] { "queued", "launching" })
        {
            await test($"{phase} write failure prevents engine invocation", async () =>
            {
                var (vm, worker, _, _) = await Model("save-fail-" + phase, phase);
                await (await Start(vm, "fail-save-" + phase));
                Check(worker.Runs.Count == 0 && vm.Tasks.Single().State == "failed" && vm.HasMessage, "Worker launched without durable record");
            });
        }
        await test("draft remains enabled while submitted request is immutable", async () =>
        {
            var (vm, worker, _, _) = await Model("draft");
            vm.Threads = 1;
            var first = await Start(vm, "draft-original"); await Until(() => worker.Runs.Count == 1);
            var captured = worker.Runs[0];
            Check(vm.CanEdit && vm.HasUnfinishedTasks, "Draft controls disabled during execution");
            await vm.SetSourcesAsync([Path.Combine(scratch, "draft-new.png")]); vm.SelectPreset("webp-share");
            vm.EngineIndex = 1; vm.Config = vm.Config with { Server = "new-nas.local", NasHosts = ["new-nas.local"] };
            Check(captured.Sources[0].EndsWith("draft-original.jpg") && captured.Options.Format == "jpeg"
                && captured.Engine == "pc" && captured.Config.Server != "new-nas.local", "Draft mutated submitted snapshot");
            Check(vm.CanEdit && vm.HasUnfinishedTasks && !vm.IsLocked, "Close guard must consider tasks despite draft ready state");
            worker.Finish(captured.JobId, "succeeded"); await first;
        });
        await test("cancel all stops active tasks and all waiters before slots reopen", async () =>
        {
            var (vm, worker, _, _) = await Model("close-all");
            var first = await Start(vm, "close-a"); await Until(() => worker.Runs.Count == 1);
            var second = await Start(vm, "close-b"); await Until(() => vm.Tasks.Any(t => t.IsQueued));
            await vm.CancelAllAndWaitAsync(); await Task.WhenAll(first, second);
            Check(worker.Runs.Count == 1 && !vm.HasUnfinishedTasks, "Close launched a waiter or left active work");
        });
        await test("cancel all preserves unknown when recheck cannot confirm stopping", async () =>
        {
            var (vm, worker, _, _) = await Model("close-unknown");
            worker.InspectState = "unknown";
            var run = await Start(vm, "close-unknown"); await Until(() => worker.Runs.Count == 1);
            worker.Finish(worker.Runs[0].JobId, "unknown"); await run;
            await vm.CancelAllAndWaitAsync(); Check(vm.HasUnfinishedTasks && vm.Tasks.Single().IsUnknown, "Unconfirmed task treated as stopped");
        });
        await test("failure and PC/NAS output notifications reach bindings", () =>
        {
            foreach (var engine in new[] { "pc", "nas" })
            {
                var task = new CompressionTaskViewModel(new() { Engine = engine, Sources = ["file.jpg"] }, engine == "nas" ? 1 : 0);
                var hasFailures = false; var canOpen = false;
                task.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(task.HasFailures)) hasFailures = task.HasFailures;
                    if (e.PropertyName == nameof(task.CanOpenOutput)) canOpen = task.CanOpenOutput;
                };
                task.Apply(new() { JobId = task.JobId, Type = "file", State = "failed", Total = 2, Completed = 1, Failed = 1, Path = "bad.jpg", Message = "fixture" });
                task.Apply(new() { JobId = task.JobId, Type = "finished", State = "partial", Total = 2, Completed = 2, Failed = 1, Succeeded = 1, Output = "/local/out", OpenOutput = engine == "nas" ? @"\\nas\photos\out" : "" });
                Check(hasFailures && canOpen && task.OpenOutputPath == (engine == "pc" ? "/local/out" : @"\\nas\photos\out"), "Derived binding or output mapping lost");
            }
            return Task.CompletedTask;
        });
        await test("deferred UI dispatch finalizes exactly once and ignores late events", async () =>
        {
            var context = SynchronizationContext.Current!;
            var (vm, worker, config, disk) = await Model("deferred", dispatch: action => context.Post(_ => action(), null));
            var run = await Start(vm, "deferred"); await Until(() => worker.Runs.Count == 1);
            var task = vm.Tasks.Single(); worker.Finish(task.JobId, "succeeded"); await run;
            vm.Apply(new() { JobId = task.JobId, Type = "finished", State = "unknown" }); await Task.Delay(30);
            Check(vm.Tasks.Count == 0 && task.State == "succeeded" && disk.Deletes == 1, "Late dispatch corrupted finalized task");
            Check(!Directory.GetFiles(Path.Combine(config.DirectoryPath, "tasks"), "*.json").Any(), "Finished record retained");
        });
        await test("finished record survives deletion failure and recovers without execution", async () =>
        {
            var (vm, worker, config, disk) = await Model("delete-fail"); disk.FailDelete = true;
            var run = await Start(vm, "delete-fail"); await Until(() => worker.Runs.Count == 1);
            var id = worker.Runs[0].JobId; worker.Finish(id, "succeeded"); await run;
            var record = Wire.Decode<TaskRecord>(await File.ReadAllTextAsync(Path.Combine(config.DirectoryPath,"tasks",id+".json")));
            Check(record.Phase == "finished" && record.Result?.State == "succeeded" && !vm.HasUnfinishedTasks, "Confirmed result not durably retained");
            var recoveredWorker = new ControlledWorker();
            var recovered = new MainViewModel(recoveredWorker, config, "fixture"); await recovered.InitializeAsync();
            Check(recovered.Tasks.Count == 0 && recoveredWorker.Runs.Count == 0, "Finished recovery reran or retained task");
        });
    }

    private sealed class ControlledWorker : IWorkerClient
    {
        public List<WorkerRequest> Runs { get; } = [];
        private readonly Dictionary<string, TaskCompletionSource<string>> results = new();
        public string InspectState { get; set; } = "succeeded";
        public void Finish(string id, string state) => results[id].TrySetResult(state);
        public async Task ExecuteAsync(WorkerRequest request, Action<WorkerEvent> emit)
        {
            if (request.Operation == "probe") { emit(new() { JobId = request.JobId, Type = "probe", PcAvailable = true }); return; }
            if (request.Operation == "cancel")
            {
                emit(new() { JobId = request.JobId, Type = "cancelRequested" });
                Finish(request.JobId, "cancelled"); return;
            }
            if (request.Operation == "inspect") { emit(new() { JobId = request.JobId, Type = "finished", State = InspectState }); return; }
            results[request.JobId] = new(TaskCreationOptions.RunContinuationsAsynchronously); Runs.Add(request);
            emit(new() { JobId = request.JobId, Type = "manifest", Total = 1 });
            var state = await results[request.JobId].Task;
            emit(new() { JobId = request.JobId, Type = "finished", State = state, Total = 1, Completed = state == "succeeded" ? 1 : 0, Succeeded = state == "succeeded" ? 1 : 0 });
        }
    }
    private sealed class FaultStore(ITaskStore inner) : ITaskStore
    {
        public string? FailPhase { get; init; }
        public bool BlockLaunching { get; set; }
        public bool FailDelete { get; set; }
        public int Deletes { get; private set; }
        public TaskCompletionSource LaunchEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowLaunch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TaskRecovery> LoadAsync() => inner.LoadAsync();
        public async Task SaveAsync(TaskRecord record)
        {
            if (record.Phase == FailPhase) throw new IOException("Fixture write failure");
            if (record.Phase == "launching" && BlockLaunching) { LaunchEntered.TrySetResult(); await AllowLaunch.Task; }
            await inner.SaveAsync(record);
        }
        public Task DeleteAsync(string id)
        {
            if (FailDelete) throw new IOException("Fixture delete failure");
            Deletes++; return inner.DeleteAsync(id);
        }
    }
}
