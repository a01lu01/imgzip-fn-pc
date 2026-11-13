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
            var (vm1, worker, config, disk) = await Model("remove");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, vm1.Coordinator);
            await vm2.InitializeAsync();
            var first = await Start(vm1, "remove-first"); await Until(() => worker.Runs.Count == 1);
            var second = await Start(vm2, "remove-second"); await Until(() => vm2.CurrentTask?.IsQueued == true);
            var removed = vm2.CurrentTask!;
            await vm2.CancelTaskAsync(removed);
            worker.Finish(worker.Runs[0].JobId, "succeeded");
            await Task.WhenAll(first, second);
            Check(worker.Runs.Count == 1 && vm1.Coordinator.Tasks.Count == 0, "Removed waiter launched or was retained");
        });
        await test("cancellation wins during durable launch reservation", async () =>
        {
            var (vm, worker, _, disk) = await Model("reserve-race");
            disk.BlockLaunching = true;
            var run = await Start(vm, "race");
            await disk.LaunchEntered.Task;
            var cancel = vm.CancelTaskAsync(vm.CurrentTask!);
            disk.AllowLaunch.TrySetResult();
            await Task.WhenAll(run, cancel);
            Check(worker.Runs.Count == 0 && vm.Coordinator.Tasks.Count == 0 && vm.CurrentTask is { State: "cancelled" },
                "Task launched after cancellation during reservation");
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
                var tasks = vm.Coordinator.Tasks.ToArray();
                Check(worker.Runs.Count == 0 && tasks.All(t => t.CanResume), "Restored queue auto-started");
                var first = vm.ResumeTaskAsync(tasks[0]); await Until(() => worker.Runs.Count == 1);
                worker.Finish(tasks[0].JobId, "unknown"); await first;
                var second = vm.ResumeTaskAsync(tasks[1]); await Task.Delay(350);
                Check(worker.Runs.Count == 1 && tasks[1].IsQueued, "Unknown released its slot");
                await vm.InspectTaskAsync(tasks[0]); await Until(() => worker.Runs.Count == 2);
                worker.Finish(tasks[1].JobId, "succeeded"); await second;
                Check(!vm.HasUnfinishedTasks && vm.Coordinator.Tasks.Count == 0, "Confirmed terminal failed to release capacity");
                foreach (var phase in new[] { "launching", "running" })
                {
                    var unresolved = new WorkerRequest { Engine = engine, Sources = ["unresolved.jpg"], StateDirectory = config.DirectoryPath };
                    await disk.SaveAsync(new() { Request = unresolved, Phase = phase });
                    await disk.SaveAsync(new() { Request = unresolved with { JobId = Guid.NewGuid().ToString("N"), Sources = ["waiting.jpg"] } });
                    var restartedWorker = new ControlledWorker();
                    var restarted = new MainViewModel(restartedWorker, config, "fixture"); await restarted.InitializeAsync();
                    var waiter = restarted.Coordinator.Tasks.Single(t => t.CanResume);
                    var resumed = restarted.ResumeTaskAsync(waiter); await Task.Delay(350);
                    Check(restartedWorker.Runs.Count == 0 && waiter.IsQueued, $"Recovered {phase} failed to hold its slot");
                    await restarted.InspectTaskAsync(restarted.Coordinator.Tasks.Single(t => t.IsUnknown));
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
            Check(worker.Runs.Count == 0 && vm.Coordinator.Tasks.All(t => t.CanResume && t.CanRemove), "Queue incorrectly restored");
            await vm.CancelTaskAsync(vm.Coordinator.Get(records[0].Request.JobId)!);
            var remaining = vm.Coordinator.Tasks.Single(); var run = vm.ResumeTaskAsync(remaining);
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
            Check(vm.Coordinator.Tasks.Count == 1 && vm.CurrentTask is { IsUnknown: true }, "Legacy task missing or duplicated");
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
            Check(vm.CurrentTask is not null && vm.CanInspect && vm.HasUnfinishedTasks, "Standalone marker not registered");
            await vm.InspectAsync(); Check(vm.Coordinator.Tasks.Count == 0, "Migrated task could not be finalized");
        });
        await test("migration write failure preserves old marker and registers unresolved task", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "migration-fail")); Directory.CreateDirectory(config.DirectoryPath);
            var request = new WorkerRequest { Sources = ["legacy.jpg"] };
            var marker = Path.Combine(config.DirectoryPath, "active-job.json"); await File.WriteAllTextAsync(marker, Wire.Encode(request));
            // A file blocks creation of the tasks directory, including under administrator test accounts.
            await File.WriteAllTextAsync(Path.Combine(config.DirectoryPath, "tasks"), "blocked");
            var vm = new MainViewModel(new ControlledWorker(), config, "fixture"); await vm.InitializeAsync();
            Check(vm.CurrentTask is { IsUnknown: true } && File.Exists(marker) && vm.HasMessage, "Migration failed silently or lost legacy task");
        });
        foreach (var phase in new[] { "queued", "launching" })
        {
            await test($"{phase} write failure prevents engine invocation", async () =>
            {
                var (vm, worker, _, _) = await Model("save-fail-" + phase, phase);
                await (await Start(vm, "fail-save-" + phase));
                Check(worker.Runs.Count == 0 && vm.CurrentTask is { State: "failed" } && vm.HasMessage, "Worker launched without durable record");
            });
        }
        await test("draft is frozen while running and the submitted request stays immutable", async () =>
        {
            var (vm, worker, _, _) = await Model("draft");
            vm.Threads = 1;
            var first = await Start(vm, "draft-original"); await Until(() => worker.Runs.Count == 1);
            var captured = worker.Runs[0];
            // 任务承办后草稿只读：界面上按钮禁用，模型层的来源入口也同样拒绝修改。
            Check(!vm.CanEdit && vm.HasUnfinishedTasks, "Draft stayed editable during execution");
            await vm.SetSourcesAsync([Path.Combine(scratch, "draft-new.png")]); vm.ClearSources();
            Check(vm.SourcePath.EndsWith("draft-original.jpg"), "Draft accepted a new source while its task ran");
            // 已提交的请求是快照：草稿怎么变都不影响它。
            Check(captured.Sources[0].EndsWith("draft-original.jpg") && captured.Options.Format == "jpeg"
                && captured.Engine == "pc" && captured.Config.Server != "new-nas.local", "Draft mutated submitted snapshot");
            // 关闭守卫看的是本窗口还有没有未结束的任务（不是草稿是否可编辑）。
            Check(vm.HasUnfinishedTasks && vm.CurrentTaskUnfinished, "Close guard must consider tasks despite draft ready state");
            worker.Finish(captured.JobId, "succeeded"); await first;
        });
        await test("cancel all stops active tasks and all waiters before slots reopen", async () =>
        {
            var (vm1, worker, config, disk) = await Model("close-all");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, vm1.Coordinator);
            await vm2.InitializeAsync();
            var first = await Start(vm1, "close-a"); await Until(() => worker.Runs.Count == 1);
            var second = await Start(vm2, "close-b"); await Until(() => vm2.CurrentTask?.IsQueued == true);
            await vm1.CancelAllAndWaitAsync(); await Task.WhenAll(first, second);
            Check(worker.Runs.Count == 1 && !vm1.HasUnfinishedTasks, "Close launched a waiter or left active work");
        });
        await test("cancel all preserves unknown when recheck cannot confirm stopping", async () =>
        {
            var (vm, worker, _, _) = await Model("close-unknown");
            worker.InspectState = "unknown";
            var run = await Start(vm, "close-unknown"); await Until(() => worker.Runs.Count == 1);
            worker.Finish(worker.Runs[0].JobId, "unknown"); await run;
            await vm.CancelAllAndWaitAsync(); Check(vm.HasUnfinishedTasks && vm.CurrentTask is { IsUnknown: true }, "Unconfirmed task treated as stopped");
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
            var task = vm.CurrentTask!; worker.Finish(task.JobId, "succeeded"); await run;
            vm.Apply(new() { JobId = task.JobId, Type = "finished", State = "unknown" }); await Task.Delay(30);
            Check(vm.Coordinator.Tasks.Count == 0 && task.State == "succeeded" && disk.Deletes == 1, "Late dispatch corrupted finalized task");
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
            Check(recovered.CurrentTask is null && recoveredWorker.Runs.Count == 0, "Finished recovery reran or retained task");
        });
        await test("shared coordinator: PC limit spans both windows and queues the rest", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "shared-pc"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var first = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await first.InitializeAsync();
            var second = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await second.InitializeAsync();
            Check(ReferenceEquals(first.Coordinator, second.Coordinator), "Windows do not share one task pool");
            Check(second.CurrentTask is null, "Second window recovered tasks already in the shared pool");
            var runA = await Start(first, "shared-a"); await Until(() => worker.Runs.Count == 1);
            var runB = await Start(second, "shared-b");
            await Task.Delay(350);
            var runningA = first.CurrentTask!;
            var queuedB = second.CurrentTask!;
            Check(worker.Runs.Count == 1 && queuedB.IsQueued && runningA.IsRunning && queuedB.JobId != runningA.JobId,
                "PC limit was applied per window instead of across windows");
            worker.Finish(worker.Runs[0].JobId, "succeeded"); await runA;
            await Until(() => worker.Runs.Count == 2);
            worker.Finish(worker.Runs[1].JobId, "succeeded"); await runB;
            Check(first.Coordinator.Tasks.Count == 0, "Finished tasks were retained after the shared run");
        });
        await test("shared coordinator: NAS tasks serialize across windows", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "shared-nas"));
            await config.SaveAsync(new() { PcConcurrency = 4, Server = "nas.local", User = "nas", KeyPath = @"C:\key", NasHosts = ["nas.local"] });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var first = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await first.InitializeAsync();
            var second = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await second.InitializeAsync();
            // UNC 来源让 NasEligible 为真，SetSourcesAsync 才会把引擎定为 NAS。
            async Task<Task> StartNas(MainViewModel vm, string name)
            {
                await vm.SetSourcesAsync([$@"\\nas.local\photos\{name}.jpg"]);
                Check(vm.EngineIndex == 1, "Engine fixture did not select NAS for UNC sources");
                return vm.StartAsync();
            }
            var runA = await StartNas(first, "shared-nas-a");
            await Until(() => worker.Runs.Count == 1 && worker.Runs[0].Engine == "nas");
            var runB = await StartNas(second, "shared-nas-b");
            await Task.Delay(350);
            Check(worker.Runs.Count(r => r.Engine == "nas") == 1 && second.CurrentTask?.IsQueued == true, "NAS tasks ran in parallel across windows");
            worker.Finish(worker.Runs[0].JobId, "succeeded"); await runA;
            await Until(() => worker.Runs.Count == 2);
            worker.Finish(worker.Runs[1].JobId, "succeeded"); await runB;
        });
        await test("shared coordinator: closing one window leaves the other task running", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "shared-close"));
            await config.SaveAsync(new() { PcConcurrency = 2 });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var first = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await first.InitializeAsync();
            var second = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await second.InitializeAsync();
            var runA = await Start(first, "shared-close-a"); await Until(() => worker.Runs.Count == 1);
            var runB = await Start(second, "shared-close-b"); await Until(() => worker.Runs.Count == 2);
            var jobA = worker.Runs[0].JobId; var jobB = worker.Runs[1].JobId;
            // 关闭第一个窗口：只取消它自己的任务，第二个继续。任务池是共享的，按 JobId 断言。
            first.Attachment.Dispose();
            await first.CancelAsync(); await runA;
            Check(coordinator.Get(jobA) is { TerminalConfirmed: true }, "Closed window left its task unresolved");
            Check(coordinator.Get(jobB) is { TerminalConfirmed: false } running && running.IsRunning, "Closing one window stopped the other task");
            worker.Finish(jobB, "succeeded"); await runB;
            Check(coordinator.Get(jobB) is null, "Finished task was not removed from the shared pool");
        });
        await test("staging cleaner removes only the closed job's directories", async () =>
        {
            var output = Path.Combine(scratch, "staging", "output");
            Directory.CreateDirectory(output);
            var jobId = Guid.NewGuid().ToString("N");
            var otherJob = Guid.NewGuid().ToString("N");
            foreach (var suffix in new[] { "b1", "b2" })
            {
                var staging = Path.Combine(output, $".imgzip-{jobId}-{suffix}");
                Directory.CreateDirectory(staging);
                await File.WriteAllTextAsync(Path.Combine(staging, "partial.bin"), "half-written");
            }
            var foreign = Path.Combine(output, $".imgzip-{otherJob}-b1");
            Directory.CreateDirectory(foreign);
            await File.WriteAllTextAsync(Path.Combine(foreign, "partial.bin"), "other job");
            var failures = StagingCleaner.Clean(output, jobId);
            Check(failures.Count == 0, "Staging cleaner reported a failure for an owned directory");
            Check(!Directory.Exists(Path.Combine(output, $".imgzip-{jobId}-b1")) && !Directory.Exists(Path.Combine(output, $".imgzip-{jobId}-b2")),
                "Staging cleaner left a directory of the closed job");
            Check(Directory.Exists(foreign) && File.Exists(Path.Combine(foreign, "partial.bin")), "Staging cleaner deleted another job's staging directory");
        });
        await test("recovery treats a dead recorded process as interrupted", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "force-dead"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var record = new TaskRecord
            {
                Request = new() { Sources = [Path.Combine(scratch, "force-dead.jpg")], StateDirectory = config.DirectoryPath },
                Phase = "running", WorkerPid = 999999, WorkerStartedAt = DateTimeOffset.UtcNow
            };
            await disk.SaveAsync(record);
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk, null, new FixedProbe(false));
            var vm = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm.InitializeAsync();
            Check(vm.Coordinator.Tasks.Count == 0 && !vm.HasUnfinishedTasks, "Dead recorded process neither finalized nor removed");
            Check(worker.Runs.Count == 0, "Dead recorded process was relaunched");
            Check(!Directory.EnumerateFiles(Path.Combine(config.DirectoryPath, "tasks"), "*.json").Any(), "Interrupted record was not cleaned up");
        });
        await test("recovery adopts a live recorded process and keeps its slot", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "force-alive"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var record = new TaskRecord
            {
                Request = new() { Sources = [Path.Combine(scratch, "force-alive.jpg")], StateDirectory = config.DirectoryPath },
                Phase = "running", WorkerPid = 12345, WorkerStartedAt = DateTimeOffset.UtcNow
            };
            await disk.SaveAsync(record);
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk, null, new FixedProbe(true));
            var vm1 = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm1.InitializeAsync();
            var adopted = vm1.CurrentTask!;
            Check(adopted.JobId == record.Request.JobId && adopted.State == "running", "Live recorded process was not adopted as running");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm2.InitializeAsync();
            var run = await Start(vm2, "force-alive-queued");
            await Task.Delay(350);
            Check(worker.Runs.Count == 0, "A new task started while the adopted task held the only PC slot");
            var queued = vm2.CurrentTask!;
            Check(queued.IsQueued, "Adopted task released its slot");
            await vm2.CancelTaskAsync(queued); await run;
        });
        await test("recovery without a process identity stays unknown and holds its slot", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "force-nopid"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var record = new TaskRecord
            {
                Request = new() { Sources = [Path.Combine(scratch, "force-nopid.jpg")], StateDirectory = config.DirectoryPath },
                Phase = "running", WorkerPid = 0
            };
            await disk.SaveAsync(record);
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var vm1 = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm1.InitializeAsync();
            var unresolved = vm1.CurrentTask!;
            Check(unresolved.State == "unknown" && unresolved.CanInspect, "Record without a process identity was not kept for inspection");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm2.InitializeAsync();
            var run = await Start(vm2, "force-nopid-queued");
            await Task.Delay(350);
            Check(worker.Runs.Count == 0 && vm2.CurrentTask?.IsQueued == true, "Unresolved task released its slot");
            await vm2.CancelTaskAsync(vm2.CurrentTask!); await run;
        });
        await test("force close retains records when termination is unconfirmed", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "force-unconfirmed"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new StoppableWorker { TerminateSucceeds = false };
            var coordinator = new TaskCoordinator(worker, disk);
            var vm = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm.InitializeAsync();
            var run = await Start(vm, "force-unconfirmed");
            await Until(() => worker.Runs.Count == 1 && vm.CurrentTask is { State: "running" });
            var task = vm.CurrentTask!;
            var report = await vm.ForceCloseAsync();
            var saved = Wire.Decode<TaskRecord>(await File.ReadAllTextAsync(Path.Combine(config.DirectoryPath, "tasks", task.JobId + ".json")));
            Check(report.Unconfirmed == 1 && report.Terminated == 0 && !report.FullyStopped, "Unconfirmed termination was reported as stopped");
            Check(worker.Terminated.Count == 1 && worker.Terminated[0] == task.JobId, "Force close did not ask the tracker to terminate the job");
            Check(task.State == "unknown" && task.IsUnknown, "Unconfirmed task was presented as resolved");
            Check(saved.Phase == "running" && saved.WorkerPid == StoppableWorker.ProcessIdentifier, "Retained record lost its running phase or process identity");
            Check(vm.HasUnfinishedTasks, "Unconfirmed task no longer held capacity");
            worker.Finish(task.JobId, "cancelled"); await run;
        });
        await test("force close deletes records when termination is confirmed", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "force-stopped"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new StoppableWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var vm = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm.InitializeAsync();
            var run = await Start(vm, "force-stopped");
            await Until(() => worker.Runs.Count == 1 && vm.CurrentTask is { State: "running" });
            var task = vm.CurrentTask!;
            var report = await vm.ForceCloseAsync();
            Check(report.Terminated == 1 && report.Unconfirmed == 0 && report.FullyStopped, "Confirmed termination was not reported as stopped");
            Check(worker.Terminated.Count == 1 && worker.Terminated[0] == task.JobId, "Force close did not terminate the tracked job");
            Check(vm.Coordinator.Tasks.Count == 0 && !vm.HasUnfinishedTasks, "Terminated task stayed in the pool");
            Check(!File.Exists(Path.Combine(config.DirectoryPath, "tasks", task.JobId + ".json")), "Terminated task record was retained");
            worker.Finish(task.JobId, "cancelled"); await run;
        });
        await test("force close terminates an adopted process by its recorded pid", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "force-adopted"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var record = new TaskRecord
            {
                Request = new() { Sources = [Path.Combine(scratch, "force-adopted.jpg")], StateDirectory = config.DirectoryPath },
                Phase = "running", WorkerPid = 12345, WorkerStartedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
            };
            await disk.SaveAsync(record);
            var worker = new StoppableWorker { TerminateSucceeds = true };
            var coordinator = new TaskCoordinator(worker, disk, null, new FixedProbe(true));
            var vm = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm.InitializeAsync();
            var adopted = vm.CurrentTask!;
            Check(adopted.JobId == record.Request.JobId && adopted.State == "running", "Recorded process was not adopted as running before force close");
            var report = await vm.ForceCloseAsync();
            Check(report.Terminated == 1 && report.Unconfirmed == 0, "Adopted process termination was not reported as stopped");
            // 核心：本进程没有该进程的句柄，PID 必须由记录传下去，否则 TerminateAsync 无从下手。
            Check(worker.TerminatedPids.Contains(record.WorkerPid), "Force close did not pass the recorded pid to TerminateAsync");
            Check(worker.Terminated.Contains(adopted.JobId), "Force close did not terminate the adopted job");
            Check(!Directory.EnumerateFiles(Path.Combine(config.DirectoryPath, "tasks"), "*.json").Any(), "Adopted task record survived a confirmed force close");
        });
        await test("job directory is removed when its task finishes", async () =>
        {
            var (vm, worker, config, _) = await Model("job-clean");
            var run = await Start(vm, "job-clean");
            await Until(() => worker.Runs.Count == 1 && vm.CurrentTask is { State: "running" });
            var jobs = Path.Combine(config.DirectoryPath, "jobs");
            var jobDirectory = Path.Combine(jobs, worker.Runs[0].JobId);
            Directory.CreateDirectory(jobDirectory);
            await File.WriteAllTextAsync(Path.Combine(jobDirectory, "worker.pid"), Wire.Encode(new { pid = 4321, startedAt = DateTimeOffset.UtcNow }));
            await File.WriteAllTextAsync(Path.Combine(jobDirectory, "cancel"), "cancel");
            // 另一个任务的目录没有参与本次收尾，必须原样保留。
            var unrelated = Path.Combine(jobs, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(unrelated);
            await File.WriteAllTextAsync(Path.Combine(unrelated, "worker.pid"), "{}");
            worker.Finish(worker.Runs[0].JobId, "succeeded"); await run;
            Check(!Directory.Exists(jobDirectory), "Finished task left its job directory behind");
            Check(Directory.Exists(unrelated), "Finished task deleted another job's directory");
        });
        await test("job directory survives an unconfirmed force close", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "job-unconfirmed"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new StoppableWorker { TerminateSucceeds = false };
            var coordinator = new TaskCoordinator(worker, disk);
            var vm = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm.InitializeAsync();
            var run = await Start(vm, "job-unconfirmed");
            await Until(() => worker.Runs.Count == 1 && vm.CurrentTask is { State: "running" });
            var jobId = vm.CurrentTask!.JobId;
            var jobDirectory = Path.Combine(config.DirectoryPath, "jobs", jobId);
            Directory.CreateDirectory(jobDirectory);
            await File.WriteAllTextAsync(Path.Combine(jobDirectory, "worker.pid"),
                Wire.Encode(new { pid = StoppableWorker.ProcessIdentifier, startedAt = DateTimeOffset.UtcNow }));
            var report = await vm.ForceCloseAsync();
            Check(report.Unconfirmed == 1, "Unconfirmed termination was reported as stopped");
            // worker.pid 是下次启动判定死活的最后证据，未确认终止时不得删除。
            Check(File.Exists(Path.Combine(jobDirectory, "worker.pid")), "Unconfirmed force close deleted the job directory");
            worker.Finish(jobId, "cancelled"); await run;
        });
        await test("startup sweep removes job directories without records", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "job-sweep"));
            Directory.CreateDirectory(config.DirectoryPath);
            var jobs = Path.Combine(config.DirectoryPath, "jobs");
            var orphan = Guid.NewGuid().ToString("N");
            var tracked = Guid.NewGuid().ToString("N");
            foreach (var name in new[] { orphan, tracked, "not-a-hex-id" }) Directory.CreateDirectory(Path.Combine(jobs, name));
            var tasks = Path.Combine(config.DirectoryPath, "tasks"); Directory.CreateDirectory(tasks);
            // Sweep 只按文件名检查记录是否存在，内容无须是合法记录。
            await File.WriteAllTextAsync(Path.Combine(tasks, tracked + ".json"), "{}");
            var removed = JobDirectories.Sweep(config.DirectoryPath);
            Check(removed == 1, $"Startup sweep removed {removed} job directories instead of 1");
            Check(!Directory.Exists(Path.Combine(jobs, orphan)), "Startup sweep kept a job directory without a record");
            Check(Directory.Exists(Path.Combine(jobs, tracked)), "Startup sweep deleted a job directory that still has a record");
            Check(Directory.Exists(Path.Combine(jobs, "not-a-hex-id")), "Startup sweep deleted a directory with an invalid job id");
        });
        await test("window keeps its own task when another window submits", async () =>
        {
            var (vm1, worker, config, disk) = await Model("own-task");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, vm1.Coordinator);
            await vm2.InitializeAsync();
            var run1 = await Start(vm1, "own-a"); await Until(() => worker.Runs.Count == 1);
            var run2 = await Start(vm2, "own-b"); await Until(() => vm2.CurrentTask is not null);
            // 两个窗口各自持有不同的任务：事件按 JobId 路由，不会合并到同一个实例。
            Check(vm1.CurrentTask is { } a && vm2.CurrentTask is { } b && a.JobId != b.JobId,
                "Two windows collapsed onto the same task");
            // 第一个窗口的 CurrentTask 仍是自己发起的那一个，没有被第二个窗口的提交覆盖。
            Check(vm1.CurrentTask!.Request.Sources[0].EndsWith("own-a.jpg") && vm2.CurrentTask!.Request.Sources[0].EndsWith("own-b.jpg"),
                "Submitting in another window replaced this window's task");
            // PcConcurrency=1：第二个任务先排队，第一个结束后才启动。
            Check(vm2.CurrentTask!.IsQueued && vm1.CurrentTask!.IsRunning, "Second window's task did not queue behind the first");
            worker.Finish(worker.Runs[0].JobId, "succeeded");
            await Until(() => worker.Runs.Count == 2);
            worker.Finish(worker.Runs[1].JobId, "succeeded");
            await Task.WhenAll(run1, run2);
        });
        await test("current task survives removal and reports terminal state", async () =>
        {
            var (vm, worker, _, _) = await Model("sticky");
            var run = await Start(vm, "sticky-a"); await Until(() => worker.Runs.Count == 1);
            var task = vm.CurrentTask!;
            worker.Finish(task.JobId, "succeeded"); await run;
            // 池里已经移除。
            Check(vm.Coordinator.Tasks.Count == 0, "Finished task was not removed from the pool");
            // 但窗口还持有它：CurrentTask 是粘性的，结果态仍可展示。
            Check(ReferenceEquals(vm.CurrentTask, task) && vm.CurrentTask!.State == "succeeded",
                "Window lost its result after the task left the pool");
            Check(!vm.CurrentTaskUnfinished, "Finished task still reported as unfinished");
            // 摘要是窗口自己那一句话：任务自己的 StateTitle（“已完成”），不是池级计数。
            Check(vm.TaskSummary.StartsWith("已完成", StringComparison.Ordinal) && !vm.TaskSummary.Contains("共 "),
                $"Summary did not report completion: {vm.TaskSummary}");
        });
        await test("task summary describes only this window's task", async () =>
        {
            var (vm1, worker, config, disk) = await Model("summary");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, vm1.Coordinator);
            await vm2.InitializeAsync();
            var run1 = await Start(vm1, "summary-a"); await Until(() => worker.Runs.Count == 1);
            var run2 = await Start(vm2, "summary-b"); await Until(() => vm2.CurrentTask is not null);
            // 每个窗口的摘要都只说自己那一个任务，不出现"共 N 个任务"的池级统计。
            Check(!vm1.TaskSummary.Contains("共 "), $"Summary leaked pool counts: {vm1.TaskSummary}");
            Check(!vm2.TaskSummary.Contains("共 "), $"Summary leaked pool counts: {vm2.TaskSummary}");
            Check(vm1.TaskSummary != vm2.TaskSummary, "Two windows produced an identical summary");
            // 第一个窗口在跑，第二个在排队：各自的状态才是摘要的主体。
            Check(!vm1.TaskSummary.Contains("排队中") && vm2.TaskSummary.StartsWith("排队中", StringComparison.Ordinal),
                $"Summary did not describe each window's own task: '{vm1.TaskSummary}' / '{vm2.TaskSummary}'");
            worker.Finish(worker.Runs[0].JobId, "succeeded");
            await Until(() => worker.Runs.Count == 2);
            worker.Finish(worker.Runs[1].JobId, "succeeded");
            await Task.WhenAll(run1, run2);
        });
        await test("closing one window leaves the other window's task running", async () =>
        {
            var (vm1, worker, config, disk) = await Model("window-close-one");
            var coordinator = vm1.Coordinator;
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm2.InitializeAsync();
            var run1 = await Start(vm1, "wc-a"); await Until(() => worker.Runs.Count == 1);
            var run2 = await Start(vm2, "wc-b");
            // 第二个任务在排队（PcConcurrency=1）
            await Until(() => vm2.CurrentTask is not null);
            var taskB = vm2.CurrentTask!;
            // 只关第一个窗口的任务
            var report = await vm1.ForceCloseAsync();
            Check(report.Terminated == 1 && report.Unconfirmed == 0, $"Window close did not terminate exactly its own task: {report.Terminated}/{report.Unconfirmed}");
            // 第二个任务必须还活着
            Check(coordinator.Tasks.Contains(taskB) && !taskB.TerminalConfirmed, "Closing one window killed another window's task");
            // 第一个窗口自己的任务已经取消（关闭只作用在 CurrentTask 上，不波及其他窗口）
            Check(vm1.CurrentTask is { State: "cancelled" }, "Own task was not cancelled");
            // 名额随第一个任务释放：第二个窗口的排队任务应当真的启动，而不是被一起掐死。
            await Until(() => worker.Runs.Count == 2);
            Check(vm2.CurrentTask is { } runningB && ReferenceEquals(runningB, taskB) && !taskB.TerminalConfirmed,
                "Other window's task did not start after its slot was released");
            await vm2.CancelTaskAsync(taskB);
            await Task.WhenAll(run1, run2);
        });
        await test("force close without a job id still stops everything", async () =>
        {
            // 应用退出走的是全局强制关闭：用能真正终止进程的执行层替身（生产里的 WorkerClient 实现 IWorkerTracker），
            // 才能覆盖"杀进程 + 写终态 + 移出任务池"这条真实路径。
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "window-close-all"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new StoppableWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var vm1 = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm1.InitializeAsync();
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm2.InitializeAsync();
            var run1 = await Start(vm1, "wca-a"); await Until(() => worker.Runs.Count == 1);
            var run2 = await Start(vm2, "wca-b");
            await Until(() => vm2.CurrentTask is not null);
            var report = await coordinator.ForceCloseAsync();
            Check(report.Terminated == 2, $"Application exit did not stop every task: {report.Terminated}");
            Check(coordinator.Tasks.Count == 0 && !coordinator.HasUnfinishedTasks, "Stopped tasks were retained");
            // 执行层替身不会自己醒来：手动结束仍挂起的运行，两个 StartAsync 才能返回。
            // 循环而不是一次性枚举，避免实现回退时任务从队列里溜出来把测试挂死。
            for (var i = 0; i < 100 && !Task.WhenAll(run1, run2).IsCompleted; i++)
            {
                await Task.Delay(10);
                foreach (var launched in worker.Runs.ToArray()) worker.Finish(launched.JobId, "cancelled");
            }
            await Task.WhenAll(run1, run2);
        });
        await test("window keeps its draft visible but read-only while the task runs", async () =>
        {
            var (vm, worker, _, _) = await Model("window-single-use");
            Check(vm.ShowDraft && !vm.HasCurrentTask, "Fresh window should show its draft");
            var run = await Start(vm, "single-use-a"); await Until(() => worker.Runs.Count == 1);
            // 任务进行中：草稿区必须还在（用户刚确认的设置、来源、输出路径都还要看得到），
            // 但不能再编辑或再启动第二个任务。
            Check(vm.ShowDraft && vm.HasCurrentTask, "Draft vanished the moment a task started");
            Check(!vm.CanEdit && !vm.CanStartTask && !vm.ShowStartButton, "Draft stayed editable while its task ran");
            Check(vm.CurrentTaskUnfinished, "Running task was not reported as unfinished");
            worker.Finish(worker.Runs[0].JobId, "succeeded"); await run;
            // 任务结束后草稿区收起：窗口只留结果，不做第二次任务。
            Check(!vm.ShowDraft && vm.HasCurrentTask, "Draft stayed open after the task finished");
            Check(!vm.CurrentTaskUnfinished, "Finished task was still reported as unfinished");
        });
        await test("finished window reports itself idle while another window still runs", async () =>
        {
            // 关闭守卫用的是窗口级 CurrentTaskUnfinished，不是池级 HasUnfinishedTasks。
            // 两者在「自己的任务结束、别人的任务还在跑」时给出相反答案——这正是关掉窗口 A
            // 会不会误伤窗口 B 的分界线。
            var (vm1, worker, config, disk) = await Model("window-idle-vs-busy");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, vm1.Coordinator);
            await vm2.InitializeAsync();
            var run1 = await Start(vm1, "idle-a"); await Until(() => worker.Runs.Count == 1);
            var run2 = await Start(vm2, "idle-b"); await Until(() => vm2.CurrentTask is not null);
            // 先结束 A 窗口自己的任务；PcConcurrency=1 会接着把 B 放出来
            worker.Finish(worker.Runs[0].JobId, "succeeded"); await run1;
            await Until(() => worker.Runs.Count == 2);
            // A 窗口：自己的任务结束 → 自认为空闲
            Check(!vm1.CurrentTaskUnfinished, "Finished window still reported unfinished work of its own");
            // B 窗口的任务还在跑 → 全局确实还有未结束任务
            Check(vm2.Coordinator.HasUnfinishedTasks, "Coordinator lost track of the running task");
            // 关键区分：池级为真、窗口级为假。若把守卫接回池级，这条会失败。
            Check(vm2.CurrentTaskUnfinished && !vm1.CurrentTaskUnfinished,
                "Window-level close guard collapsed into the pool-level one");
            worker.Finish(worker.Runs[1].JobId, "succeeded");
            await Task.WhenAll(run1, run2);
        });
        await test("queued task explains why it is waiting", async () =>
        {
            var (vm1, worker, config, disk) = await Model("queue-reason");
            var vm2 = new MainViewModel(worker, config, "fixture", null, disk, vm1.Coordinator);
            await vm2.InitializeAsync();
            var run1 = await Start(vm1, "qr-a"); await Until(() => worker.Runs.Count == 1);
            var run2 = await Start(vm2, "qr-b");
            await Until(() => vm2.CurrentTask is not null);
            var queued = vm2.CurrentTask!;
            // 原因由 WaitForSlotAsync 的下一轮调度写入，等它出现再断言，避免和调度赛跑。
            for (var i = 0; i < 50 && !queued.HasQueueReason; i++) await Task.Delay(10);
            Check(queued.IsQueued && queued.HasQueueReason && queued.QueueReason.Contains("PC"),
                $"Queued task did not explain itself: '{queued.QueueReason}'");
            worker.Finish(worker.Runs[0].JobId, "succeeded");
            await Until(() => worker.Runs.Count == 2);
            Check(!queued.HasQueueReason, $"Queue reason outlived the wait: '{queued.QueueReason}'");
            worker.Finish(worker.Runs[1].JobId, "succeeded");
            await Task.WhenAll(run1, run2);
        });
        await test("nas queue reason names the NAS engine", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "queue-reason-nas"));
            await config.SaveAsync(new() { PcConcurrency = 4, Server = "nas.local", User = "nas", KeyPath = @"C:\key", NasHosts = ["nas.local"] });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var first = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await first.InitializeAsync();
            var second = new MainViewModel(worker, config, "fixture", null, disk, coordinator); await second.InitializeAsync();
            // UNC 来源让 NasEligible 为真，SetSourcesAsync 才会把引擎定为 NAS。
            async Task<Task> StartNas(MainViewModel vm, string name)
            {
                await vm.SetSourcesAsync([$@"\\nas.local\photos\{name}.jpg"]);
                Check(vm.EngineIndex == 1, "Engine fixture did not select NAS for UNC sources");
                return vm.StartAsync();
            }
            var runA = await StartNas(first, "qr-nas-a");
            await Until(() => worker.Runs.Count == 1 && worker.Runs[0].Engine == "nas");
            var runB = await StartNas(second, "qr-nas-b");
            await Until(() => second.CurrentTask?.IsQueued == true);
            var queued = second.CurrentTask!;
            for (var i = 0; i < 50 && !queued.HasQueueReason; i++) await Task.Delay(10);
            Check(queued.HasQueueReason && queued.QueueReason.Contains("NAS"),
                $"NAS queue reason did not name the NAS engine: '{queued.QueueReason}'");
            worker.Finish(worker.Runs[0].JobId, "succeeded"); await runA;
            await Until(() => worker.Runs.Count == 2);
            Check(!queued.HasQueueReason, $"NAS queue reason outlived the wait: '{queued.QueueReason}'");
            worker.Finish(worker.Runs[1].JobId, "succeeded"); await runB;
        });
        await test("staging sweep finds numbered output directories but leaves other jobs alone", async () =>
        {
            // 输出目录是「来源根目录 + _compressed」的兄弟目录，worker 已存在时会加 _2、_3 后缀。
            var parent = Path.Combine(scratch, "sweep", "numbered");
            var source = Path.Combine(parent, "src");
            Directory.CreateDirectory(source);
            var jobId = Guid.NewGuid().ToString("N");
            var otherJob = Guid.NewGuid().ToString("N");
            async Task<string> Stage(string output)
            {
                var staging = Path.Combine(parent, output, $".imgzip-{jobId}-b1");
                Directory.CreateDirectory(staging);
                await File.WriteAllTextAsync(Path.Combine(staging, "partial.bin"), "half-written");
                return staging;
            }
            var plain = await Stage("src_compressed");
            var numbered = await Stage("src_compressed_2");
            var foreign = Path.Combine(parent, "src_compressed_3", $".imgzip-{otherJob}-b1");
            Directory.CreateDirectory(foreign);
            await File.WriteAllTextAsync(Path.Combine(foreign, "partial.bin"), "other job");
            // 名字前缀相同但不是编号后缀：整个目录都不该被当成输出目录。
            var backup = Path.Combine(parent, "src_compressed-backup", $".imgzip-{jobId}-b1");
            Directory.CreateDirectory(backup);
            await File.WriteAllTextAsync(Path.Combine(backup, "partial.bin"), "backup copy");
            var failures = StagingCleaner.SweepSources([source], jobId);
            Check(failures.Count == 0, "Staging sweep reported a failure for directories it owns");
            Check(!Directory.Exists(plain) && !Directory.Exists(numbered), "Staging sweep left this job's staging directories behind");
            Check(Directory.Exists(foreign) && File.Exists(Path.Combine(foreign, "partial.bin")), "Staging sweep deleted another job's staging directory");
            Check(Directory.Exists(backup) && File.Exists(Path.Combine(backup, "partial.bin")), "Staging sweep deleted a directory whose name is not a numbered output");
        });
        await test("recovering a dead task clears its staging directories", async () =>
        {
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "dead-staging"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var parent = Path.Combine(scratch, "dead-staging");
            var source = Path.Combine(parent, "src");
            Directory.CreateDirectory(source);
            var record = new TaskRecord
            {
                Request = new() { Sources = [source], StateDirectory = config.DirectoryPath },
                Phase = "running", WorkerPid = 999999, WorkerStartedAt = DateTimeOffset.UtcNow
            };
            await disk.SaveAsync(record);
            var staging = Path.Combine(source + "_compressed", $".imgzip-{record.Request.JobId}-b1");
            Directory.CreateDirectory(staging);
            await File.WriteAllTextAsync(Path.Combine(staging, "partial.bin"), "half-written");
            var worker = new ControlledWorker();
            var coordinator = new TaskCoordinator(worker, disk, null, new FixedProbe(false));
            var vm = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm.InitializeAsync();
            Check(!Directory.Exists(staging), "Recovered dead task left its staging directory behind");
            Check(vm.CurrentTask is { State: "cancelled" }, "Interrupted task was not finalized as cancelled");
            Check(vm.Coordinator.Tasks.Count == 0 && !vm.HasUnfinishedTasks, "Interrupted task stayed in the pool");
            Check(worker.Runs.Count == 0, "Interrupted task was relaunched instead of finalized");
        });
        await test("force close reports staging directories it could not remove", async () =>
        {
            // 句柄独占只在 Windows 上阻止目录删除（POSIX 允许删除已打开的文件）。
            if (!OperatingSystem.IsWindows()) return;
            var config = new ConfigStore(Path.Combine(scratch, "lifecycle", "force-staging"));
            await config.SaveAsync(new() { PcConcurrency = 1 });
            var disk = new TaskStore(config.DirectoryPath);
            var worker = new StoppableWorker();
            var coordinator = new TaskCoordinator(worker, disk);
            var vm = new MainViewModel(worker, config, "fixture", null, disk, coordinator);
            await vm.InitializeAsync();
            var run = await Start(vm, "force-staging");
            await Until(() => worker.Runs.Count == 1 && vm.CurrentTask is { State: "running" });
            var task = vm.CurrentTask!;
            var output = Path.Combine(scratch, "force-staging", "output");
            var staging = Path.Combine(output, $".imgzip-{task.JobId}-b1");
            Directory.CreateDirectory(staging);
            var locked = Path.Combine(staging, "partial.bin");
            await File.WriteAllTextAsync(locked, "half-written");
            // 强制关闭只按 task.OutputPath 清暂存；执行层替身不产生 Output，
            // 就补一条真实 worker 也会发的 manifest（它才是 Output 的第一个携带者）。
            vm.Apply(new() { JobId = task.JobId, Type = "manifest", State = "ready", Total = 1, Output = output });
            await Until(() => task.OutputPath == output);
            using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            var report = await vm.ForceCloseAsync();
            Check(report.Terminated == 1 && report.CleanupErrors.Count > 0, "Force close did not report the staging directory it could not remove");
            Check(report.CleanupErrors.Any(failure => failure.Contains($".imgzip-{task.JobId}")), "Reported path is not the staging directory that survived");
            Check(vm.Message.Contains("无法删除"), $"Force close did not surface the cleanup failure: '{vm.Message}'");
            Check(Directory.Exists(staging), "Locked staging directory disappeared without being deleted");
            worker.Finish(task.JobId, "cancelled"); await run;
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
    private sealed class FixedProbe(bool alive) : IProcessProbe
    {
        public bool IsAlive(int processId, DateTimeOffset startedAt) => alive;
    }
    /// <summary>可终止的测试执行层：能报告进程身份，TerminateAsync 的确认结果可切换。</summary>
    private sealed class StoppableWorker : IWorkerClient, IWorkerTracker
    {
        public const int ProcessIdentifier = 24680;
        public List<WorkerRequest> Runs { get; } = [];
        public List<string> Terminated { get; } = [];
        /// <summary>最近一次终止请求携带的 PID；用于确认接管路径把记录里的 PID 传了下来。</summary>
        public List<int> TerminatedPids { get; } = [];
        public bool TerminateSucceeds { get; init; } = true;
        private readonly Dictionary<string, TaskCompletionSource<string>> results = new(StringComparer.Ordinal);
        public event Action<string, int, DateTimeOffset>? ProcessStarted;
        public void Finish(string id, string state) => results[id].TrySetResult(state);
        public Task<bool> TerminateAsync(string jobId, int processId, DateTimeOffset startedAt)
        {
            Terminated.Add(jobId); TerminatedPids.Add(processId);
            return Task.FromResult(TerminateSucceeds);
        }
        public async Task ExecuteAsync(WorkerRequest request, Action<WorkerEvent> emit)
        {
            if (request.Operation == "probe") { emit(new() { JobId = request.JobId, Type = "probe", PcAvailable = true }); return; }
            if (request.Operation == "cancel")
            {
                emit(new() { JobId = request.JobId, Type = "cancelRequested" });
                Finish(request.JobId, "cancelled"); return;
            }
            if (request.Operation == "inspect") { emit(new() { JobId = request.JobId, Type = "finished", State = "succeeded" }); return; }
            results[request.JobId] = new(TaskCreationOptions.RunContinuationsAsynchronously); Runs.Add(request);
            // 模拟 worker 报告自己的进程身份；强制关闭的“保留并标注”依赖这条链路写盘。
            ProcessStarted?.Invoke(request.JobId, ProcessIdentifier, DateTimeOffset.UtcNow);
            emit(new() { JobId = request.JobId, Type = "manifest", Total = 1 });
            var state = await results[request.JobId].Task;
            emit(new() { JobId = request.JobId, Type = "finished", State = state, Total = 1, Completed = state == "succeeded" ? 1 : 0, Succeeded = state == "succeeded" ? 1 : 0 });
        }
    }
}
