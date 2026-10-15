using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Mcp;
using StWorkbench.Toolchain;

internal static class ControlTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "st-mcp-control-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new WorkspaceService(root);
            var draft = await workspace.CreateAsync("Control", "Control.stproj");
            draft = await workspace.EditAsync(draft.SessionId, draft.Revision, (p, _) => p.Hardware.Axes.Add(new("X", "0", "mm", -100, 100) { MaximumVelocity = 10 }));
            draft = await workspace.SaveAsync(draft.SessionId, draft.Revision);
            ServerOptions Options(bool allow = true, string backend = "EcMotionSimulator") => new() { WorkspaceRoot = root, RepositoryRoot = Directory.GetCurrentDirectory(), Devices = [new() { Id = "test", AllowMotion = allow, AllowPlcControl = allow, Backend = backend, AllowedAxes = ["X"] }] };
            var fake = new FakeMotion { Hash = MotionService.ManifestHash(draft.Project) };
            await using var motion = new MotionService(workspace, Options()) { TransportFactory = _ => fake };
            await using var disabled = new MotionService(workspace, Options(false)) { TransportFactory = _ => throw new Exception("Disabled permission opened transport") };
            await Refuse(() => disabled.AcquireAsync("test", draft.SessionId, draft.Revision, 1000), "startup permission");
            await Refuse(() => motion.PrepareAsync("missing", draft.SessionId, draft.Revision, "X", "Enable", 0, 0, 0), "unknown configured device");
            Check(fake.Calls.Count == 0, "unknown device made no external calls");
            fake.Hash = new string('0', 64);
            await Refuse(() => motion.AcquireAsync("test", draft.SessionId, draft.Revision, 1000), "manifest binding");
            fake.Hash = MotionService.ManifestHash(draft.Project);
            await Refuse(() => motion.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "MoveAbsolute", 101, 5, 0), "position limits");
            await Refuse(() => motion.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "MoveAbsolute", 1, 11, 0), "velocity limits");
            await Refuse(() => motion.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "Jog", 0, 1, 30001), "jog deadline");
            var plan = await motion.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "MoveAbsolute", 3, 5, 0);
            await motion.ExecuteAsync(plan.PlanId);
            await Refuse(() => motion.ExecuteAsync(plan.PlanId), "single use plan");
            Check(fake.Calls.Count(a => a == "execute") == 1, "single execution");
            await using (var bounded = new MotionService(workspace, Options()) { TransportFactory = _ => fake })
            {
                MotionPlan? firstBounded = null;
                for (var i = 0; i < 64; i++) { var candidate = await bounded.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "Enable", 0, 0, 0); firstBounded ??= candidate; }
                await Refuse(() => bounded.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "Enable", 0, 0, 0), "bounded pending plans");
                await bounded.ExecuteAsync(firstBounded!.PlanId);
                await bounded.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "Enable", 0, 0, 0);
            }
            plan = await motion.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "MoveAbsolute", 4, 5, 0);
            draft = await workspace.WriteSourceAsync(draft.SessionId, draft.Revision, draft.Sources[0].ItemId, draft.Sources[0].Text + "\n(* edit *)");
            await Refuse(() => motion.ExecuteAsync(plan.PlanId), "revision invalidates plan");
            await Refuse(() => motion.AcquireAsync("test", draft.SessionId, draft.Revision, 1000), "unsaved draft gate");
            draft = await workspace.SaveAsync(draft.SessionId, draft.Revision);
            fake.FailureAction = "acquire";
            await Refuse(() => motion.AcquireAsync("test", draft.SessionId, draft.Revision, 1000), "ownership conflict is tool error");
            fake.FailureAction = "execute";
            plan = await motion.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "Enable", 0, 0, 0);
            await Refuse(() => motion.ExecuteAsync(plan.PlanId), "gateway failure is tool error");
            await Refuse(() => motion.ExecuteAsync(plan.PlanId), "failed command not replayed");
            fake.FailureAction = null;
            fake.UnknownExecute = true;
            plan = await motion.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "Enable", 0, 0, 0);
            await Refuse(() => motion.ExecuteAsync(plan.PlanId), "unknown submission");
            await Refuse(() => motion.ExecuteAsync(plan.PlanId), "unknown command not replayed");
            fake.UnknownExecute = false;
            var realFake = new FakeMotion { Hash = MotionService.ManifestHash(draft.Project), Backend = "EcMotion" };
            await using var physical = new MotionService(workspace, Options(backend: "EcMotion")) { TransportFactory = _ => realFake };
            await Refuse(() => physical.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "Jog", 0, 1, 1000), "physical Jog capability");
            draft = await workspace.EditAsync(draft.SessionId, draft.Revision, (p, _) => p.Hardware.Axes[0] = p.Hardware.Axes[0] with { MaximumAcceleration = 10 });
            draft = await workspace.SaveAsync(draft.SessionId, draft.Revision); realFake.Hash = MotionService.ManifestHash(draft.Project);
            await Refuse(() => physical.PrepareAsync("test", draft.SessionId, draft.Revision, "X", "MoveAbsolute", 1, 1, 0), "unconfirmed physical dynamics");
            draft = await workspace.EditAsync(draft.SessionId, draft.Revision, (p, _) => p.Hardware.Axes[0] = p.Hardware.Axes[0] with { Mechanics = new() });
            draft = await workspace.SaveAsync(draft.SessionId, draft.Revision);
            var callsBefore = realFake.Calls.Count;
            await Refuse(() => physical.AcquireAsync("test", draft.SessionId, draft.Revision, 1000), "mechanical application unsupported");
            Check(callsBefore == realFake.Calls.Count, "mechanical gate precedes connection");
            await PlcTests(workspace, draft, Options());
            await DownloadTests(workspace, Options());
            var legacy = await workspace.CreateAsync("Legacy", "Legacy.stproj");
            legacy = await workspace.SaveAsync(legacy.SessionId, legacy.Revision);
            var external = new WorkspaceService(root); var externalDraft = await external.OpenAsync("Legacy.stproj");
            externalDraft = await external.WriteSourceAsync(externalDraft.SessionId, externalDraft.Revision, externalDraft.Sources[0].ItemId, externalDraft.Sources[0].Text + "\n(* external *)");
            await external.SaveAsync(externalDraft.SessionId, externalDraft.Revision);
            var staleCalls = 0;
            var staleDevice = new DeviceService(workspace, Options(), new BuildService(workspace, Options()), new OperationJobs()) { Transport = (_, _) => { staleCalls++; throw new Exception("Stale saved manifest opened transport"); } };
            await Refuse(() => staleDevice.PrepareAsync("test", "start", legacy.SessionId, legacy.Revision, null), "external saved manifest change");
            Check(staleCalls == 0, "Saved disk revision checked before device connection");
            Console.WriteLine("PASS MCP control permissions, manifests, revisions, single-use plans, limits, ownership errors, unknown/no replay and capability gates");
        }
        finally { Directory.Delete(root, true); }
    }
    private static async Task PlcTests(WorkspaceService workspace, WorkspaceView draft, ServerOptions options)
    {
        var calls = new List<DeploymentRequest>(); var active = new string('a', 64); var state = "stopped";
        var jobs = new OperationJobs();
        var device = new DeviceService(workspace, options, new BuildService(workspace, options), jobs)
        {
            Transport = (_, request) =>
            {
                calls.Add(request);
                if (request.Action is "start" or "stop") { Check(request.ExpectedActiveBuildHash == active, "PLC mutation identity bound"); state = request.Action == "start" ? "running" : "stopped"; }
                return Task.FromResult(new DeploymentResponse(true, state, request.RequestId, "", JsonSerializer.SerializeToElement(new { build_hash = active }), null));
            }
        };
        foreach (var action in new[] { "start", "stop" })
        {
            var plan = await device.PrepareAsync("test", action, draft.SessionId, draft.Revision, null);
            var job = device.Execute(plan.PlanId); job = await Wait(jobs, job.OperationId);
            Check(job.State == "Completed" && state == (action == "start" ? "running" : "stopped"), "PLC verified " + action);
            try { device.Execute(plan.PlanId); throw new Exception("Duplicate PLC plan accepted"); } catch (InvalidOperationException) { }
        }
        Check(calls.Count(c => c.Action == "start") == 1 && calls.Count(c => c.Action == "stop") == 1, "PLC mutations never repeated");
        var before = calls.Count;
        await Refuse(() => device.PrepareAsync("absent", "start", draft.SessionId, draft.Revision, null), "unknown device");
        Check(calls.Count == before, "unknown PLC no external call");
        var disabledOptions = new ServerOptions { Devices = [new() { Id = "test" }] };
        var disabled = new DeviceService(workspace, disabledOptions, new BuildService(workspace, options), jobs) { Transport = (_, _) => throw new Exception("Unauthorized transport") };
        await Refuse(() => disabled.PrepareAsync("test", "start", draft.SessionId, draft.Revision, null), "PLC startup permission");
        var rejected = jobs.Start(() => throw new ArgumentException("validation refused before send"));
        Check((await Wait(jobs, rejected.OperationId)).State == "Failed", "known refusal must not say Unknown");
        var uncertain = jobs.Start(() => throw new IOException("connection lost after submission"));
        Check((await Wait(jobs, uncertain.OperationId)).State == "Unknown", "transport uncertainty retained");
    }
    private static async Task DownloadTests(WorkspaceService workspace, ServerOptions originalOptions)
    {
        var project = await workspace.CreateAsync("Download", "Download.stproj");
        project = await workspace.EditAsync(project.SessionId, project.Revision, (p, docs) =>
        {
            p.Hardware.Axes.Add(new("X", "0", "mm", -100, 100) { MaximumVelocity = 10 });
            docs[0] = docs[0] with { Text = "PROGRAM Main\nVAR\n Power : MC_Power;\nEND_VAR\nPower(Axis := AXIS_X, Enable := FALSE);\nEND_PROGRAM\n" };
        });
        project = await workspace.SaveAsync(project.SessionId, project.Revision);
        var options = new ServerOptions { WorkspaceRoot = originalOptions.WorkspaceRoot, RepositoryRoot = originalOptions.RepositoryRoot,
            NodeExecutable = "C:/Program Files/nodejs/node.exe", Devices = originalOptions.Devices };
        var builds = new BuildService(workspace, options);
        var build = await builds.StartAsync(project.SessionId, project.Revision);
        for (var i = 0; i < 600 && build.State is "Queued" or "Running"; i++) { await Task.Delay(100); build = builds.Read(build.OperationId); }
        Check(build.Success, "Real compiler axis candidate failed: " + build.Log);
        foreach (var (running, ready, unknown, mechanical) in new (bool, bool?, bool, bool)[] { (true, true, false, false), (false, true, false, false), (true, null, false, false), (true, false, false, false), (true, true, true, false), (true, true, false, true) })
        {
            if (mechanical)
            {
                project = await workspace.EditAsync(project.SessionId, project.Revision, (p, _) => p.Hardware.Axes[0] = p.Hardware.Axes[0] with { Mechanics = new() });
                project = await workspace.SaveAsync(project.SessionId, project.Revision);
                build = await builds.StartAsync(project.SessionId, project.Revision);
                for (var i = 0; i < 600 && build.State is "Queued" or "Running"; i++) { await Task.Delay(100); build = builds.Read(build.OperationId); }
                Check(build.Success, "Mechanical axis candidate build: " + build.Log);
            }
            var active = new string('a', 64); var state = running ? "running" : "stopped"; var calls = new List<string>(); var jobs = new OperationJobs();
            var device = new DeviceService(workspace, options, builds, jobs)
            {
                Transport = (_, request) =>
                {
                    calls.Add(request.Action);
                    if (request.Action is "stop" or "activate" or "start") Check(request.ExpectedActiveBuildHash == active, "Control identity must advance after activation");
                    var responseState = state;
                    if (request.Action == "compile") responseState = "compiled";
                    if (request.Action == "stage") responseState = "staged";
                    if (request.Action == "stop") responseState = state = "stopped";
                    if (request.Action == "activate") { active = build.BuildHash!; state = "stopped"; responseState = "activated-not-running"; }
                    if (request.Action == "start") responseState = state = "running";
                    if (unknown && request.Action == "activate") return Task.FromResult(new DeploymentResponse(false, "unknown", request.RequestId, "lost after submit", null, null));
                    return Task.FromResult(new DeploymentResponse(true, responseState, request.RequestId, "", JsonSerializer.SerializeToElement(new { build_hash = active }), null, AxisReady: ready));
                }
            };
            var plan = await device.PrepareAsync("test", "download", project.SessionId, project.Revision, build.OperationId);
            var job = await Wait(jobs, device.Execute(plan.PlanId).OperationId);
            if (ready != true || mechanical) Check(job.State == "Blocked" && !calls.Contains("activate") && !calls.Contains("stop"), "Axis missing readiness must block activation");
            else if (unknown) Check(job.State == "Unknown" && calls.Count(a => a == "activate") == 1 && !calls.Contains("start"), "Unknown activation cannot be replayed or resumed");
            else Check(job.State == "Completed" && state == (running ? "running" : "stopped") && calls.Contains("start") == running, "Download restores original state");
        }
        Console.WriteLine("PASS MCP real-compiler axis package + simulated download preserves run/stop, requires explicit axis readiness, advances identity and does not replay unknown activation");
    }
    private static async Task<OperationView> Wait(OperationJobs jobs, string id)
    {
        for (var i = 0; i < 200; i++) { var view = jobs.Read(id); if (view.State != "Running") return view; await Task.Delay(10); }
        throw new Exception("Control job did not finish");
    }
    private sealed class FakeMotion : IMotionTransport
    {
        public string Hash = ""; public string Backend = "EcMotionSimulator"; public string? FailureAction; public bool UnknownExecute; public List<string> Calls { get; } = [];
        public Task<JsonElement> RequestAsync(string action, IReadOnlyDictionary<string, object?>? parameters = null)
        {
            Calls.Add(action);
            if (action == FailureAction) return Task.FromResult(JsonSerializer.SerializeToElement(new { success = false, message = "ownership_conflict or rejected request" }));
            if (action == "execute" && UnknownExecute) throw new IOException("Disconnected after submit");
            return Task.FromResult(action == "capabilities" ? JsonSerializer.SerializeToElement(new { success = true, capabilities = new { Backend, ControllerReady = true, PhysicalBasicAxis = true, SimulatorBasicAxis = true, ManifestSha256 = Hash } }) : JsonSerializer.SerializeToElement(new { success = true, state = "Accepted" }));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Refuse(Func<Task> action, string name)
    {
        try { await action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException) { return; }
        throw new Exception("Expected refusal: " + name);
    }
}
