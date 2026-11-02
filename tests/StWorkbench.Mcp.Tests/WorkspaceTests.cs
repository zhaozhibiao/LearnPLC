using StWorkbench.Core;
using StWorkbench.Mcp;

internal static class WorkspaceTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "st-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new WorkspaceService(root);
            var created = await service.CreateAsync("Demo", "Demo/Main.stproj");
            var first = created.Sources.Single();
            var changed = await service.WriteSourceAsync(created.SessionId, created.Revision, first.ItemId, first.Text.Replace("END_PROGRAM", "counter := counter + 1;\nEND_PROGRAM"));
            Check(changed.Revision != created.Revision, "edit produces revision");
            await Reject(() => service.WriteSourceAsync(created.SessionId, created.Revision, first.ItemId, "old"), "stale draft revision");
            await service.SaveAsync(changed.SessionId, changed.Revision);
            var disk = await new ProjectStore().LoadAsync(Path.Combine(root, "Demo/Main.stproj"));
            Check(disk.Sources.Single().Text.Contains("counter :="), "ProjectStore roundtrip");
            var other = new WorkspaceService(root);
            var stale = await other.OpenAsync("Demo/Main.stproj");
            var newer = await service.WriteSourceAsync(changed.SessionId, changed.Revision, first.ItemId, first.Text + "\n(* newer *)");
            await service.SaveAsync(newer.SessionId, newer.Revision);
            await Reject(() => other.SaveAsync(stale.SessionId, stale.Revision), "cross-session overwrite");
            await Reject(() => service.OpenAsync("../outside.stproj"), "path traversal");
            await Reject(() => service.CreateAsync("Again", "Demo/Main.stproj"), "create existing manifest");
            var before = await service.ReadAsync(newer.SessionId);
            await Reject(() => service.WriteSourceAsync(newer.SessionId, "incorrect", first.ItemId, "bad"), "failed transaction");
            Check((await service.ReadAsync(newer.SessionId)).Revision == before.Revision, "failed edit preserves draft");
            var withType = await service.AddResourceAsync(newer.SessionId, newer.Revision, "Motor", ItemKind.DataType);
            var withStruct = await service.AddResourceAsync(withType.SessionId, withType.Revision, "Settings", ItemKind.DataType, "TYPE Settings : STRUCT\nMotor : DINT;\nEND_STRUCT;\nEND_TYPE\n");
            var motor = withStruct.Project.Items.Single(i => i.Name == "Motor");
            await Reject(() => service.RenameResourceAsync(withStruct.SessionId, withStruct.Revision, motor.Id, "MotorType"), "ambiguous struct field rename");
            var withFunction = await service.AddResourceAsync(withStruct.SessionId, withStruct.Revision, "MyFunction", ItemKind.Function, "FUNCTION MyFunction : BOOL\n(* MyFunction stays in comment *)\nMyFunction := TRUE;\nEND_FUNCTION\n");
            var function = withFunction.Project.Items.Single(i => i.Name == "MyFunction");
            var renamed = await service.RenameResourceAsync(withFunction.SessionId, withFunction.Revision, function.Id, "RenamedFunction");
            var functionText = renamed.Sources.Single(s => s.ItemId == function.Id).Text;
            Check(functionText.Contains("FUNCTION RenamedFunction") && functionText.Contains("(* MyFunction stays in comment *)"), "rename definition/result but preserve comments");
            var changedView = await service.ReadAsync(renamed.SessionId);
            changedView.Project.Name = "mutated returned DTO";
            Check((await service.ReadAsync(renamed.SessionId)).Project.Name != changedView.Project.Name, "detached returned DTO");
            var workerView = await service.AddResourceAsync(renamed.SessionId, renamed.Revision, "Worker", ItemKind.Program);
            var worker = workerView.Project.Items.Single(i => i.Name == "Worker");
            var configuration = new ConfigurationService(service);
            PlcTask[] tasks = [workerView.Project.Tasks[0], new() { Name = "Fast", ProgramId = worker.Id, Scheduling = PlcTaskScheduling.Realtime, Priority = 4, Cpu = 2 }];
            workerView.Project.Hardware.Axes.Add(new("Migration", "Axis1", "mm", -10, 10) { MaximumJerk = 12, Runtime = new() { DriveDemandProtection = true, DriveErrorOnly = true, DemandDeliveryEnforced = false, DemandDeliveryTolerance = .02, FollowingErrorRawPerUnit = 100, PositionDemandRawPerUnit = 200, VelocityFeedbackRawPerUnitPerSecond = 123, VelocityOffsetRawPerUnitPerSecond = 456, VelocityFeedforwardGain = .75, DriveFollowingErrorWindowRaw = 321 } });
            var schema = EngineeringSchemas.For(typeof(HardwareConfiguration)).ToJsonString();
            Check(schema.Contains("VelocityOffsetRawPerUnitPerSecond") && schema.Contains("DriveFollowingErrorWindowRaw") && schema.Contains("CycleTiming") && schema.Contains("DcSync0ShiftNanoseconds"), "MCP schema exposes migration settings");
            var configured = await configuration.ConfigureAsync(workerView.SessionId, workerView.Revision, workerView.Project.Hardware, null, tasks, null);
            Check(configured.Project.Tasks[0].Scheduling == PlcTaskScheduling.Ordinary && configured.Project.Tasks[1] is { Scheduling: PlcTaskScheduling.Realtime, Priority: 4, Cpu: 2 }, "MCP preserves ordinary and realtime task settings");
            Check(configured.Project.Hardware.Axes[0] is { MaximumJerk: 12, Runtime: { VelocityFeedforwardGain: .75, DemandDeliveryEnforced: false, DriveFollowingErrorWindowRaw: 321 } }, "MCP detached engineering fields survive update/read");
            tasks[1].Priority = 9;
            Check((await service.ReadAsync(configured.SessionId)).Project.Tasks[1].Priority == 4, "MCP scheduling settings detached from caller");
            await service.SaveAsync(configured.SessionId, configured.Revision);
            Check((await new ProjectStore().LoadAsync(Path.Combine(root, "Demo/Main.stproj"))).Project.Tasks[1].Priority == 4, "MCP multi task scheduling saved for desktop");
            var migrationReload = (await new ProjectStore().LoadAsync(Path.Combine(root, "Demo/Main.stproj"))).Project.Hardware.Axes[0];
            Check(migrationReload.Runtime is { VelocityFeedbackRawPerUnitPerSecond: 123, VelocityOffsetRawPerUnitPerSecond: 456, FollowingErrorRawPerUnit: 100, PositionDemandRawPerUnit: 200 } && migrationReload.MaximumJerk == 12, "MCP save/reopen preserves protection and velocity units");
            tasks[1].ProgramId = tasks[0].ProgramId;
            await Reject(() => configuration.ConfigureAsync(configured.SessionId, configured.Revision, configured.Project.Hardware, null, tasks, null), "MCP rejects concurrent program instances");
            Console.WriteLine("PASS MCP workspace revisions, save conflicts, confinement and ProjectStore compatibility");
        }
        finally { Directory.Delete(root, true); }
    }
    internal static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static async Task Reject(Func<Task> run, string name)
    {
        try { await run(); } catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException or IOException) { return; }
        throw new Exception("Expected refusal: " + name);
    }
}
