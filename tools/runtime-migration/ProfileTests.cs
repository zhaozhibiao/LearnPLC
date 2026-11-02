using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

namespace StWorkbench.RuntimeMigration;

internal static class ProfileTests
{
    public static async Task RunAsync(string archivePath)
    {
        var archiveJson = await File.ReadAllTextAsync(archivePath);
        var loaded = EngineeringArchive.Read(archiveJson);
        void Reject(string json)
        {
            try { EngineeringArchive.Read(json); }
            catch (InvalidDataException) { return; }
            throw new InvalidDataException("Tampered archive was accepted.");
        }
        var altered = System.Text.Json.Nodes.JsonNode.Parse(archiveJson)!;
        altered["Project"]!["Tasks"]![0]!["Scheduling"] = 1;
        Reject(altered.ToJsonString());
        altered = System.Text.Json.Nodes.JsonNode.Parse(archiveJson)!;
        altered["Sources"]![0]!["Text"] = "PROGRAM Main END_PROGRAM";
        Reject(altered.ToJsonString());
        altered = System.Text.Json.Nodes.JsonNode.Parse(archiveJson)!;
        altered["Project"]!["Hardware"]!["Axes"]![0]!["MaxPosition"] = 1000;
        Reject(altered.ToJsonString());
        var original = JsonSerializer.Serialize(loaded);
        var migrated = MigrationProfile.Create(loaded);
        void Check(bool value, string message) { if (!value) throw new InvalidDataException("FAIL: " + message); }
        Check(migrated.Project.Id != loaded.Project.Id, "migration needs a separate project identity");
        Check(migrated.Project.Id == MigrationProfile.Create(loaded).Project.Id, "migration identity reproducible");
        Check(JsonSerializer.Serialize(loaded) == original, "source archive must remain unchanged");
        Check(migrated.Sources.SequenceEqual(loaded.Sources), "ST source text and item bindings unchanged");
        var axis = migrated.Project.Hardware.Axes.Single();
        Check(axis is { Name: "X", ControllerAxis: "Axis1", Unit: "rev", MinPosition: -2, MaxPosition: 2, MaximumVelocity: 1, MaximumAcceleration: 1, MaximumDeceleration: 1, MaximumJerk: 20 }, "production axis limits preserved");
        Check(axis.Runtime is { MaximumFollowingError: .005, FollowingErrorTimeoutMilliseconds: 20, DriveDemandProtection: true, DriveErrorOnly: true, DemandDeliveryEnforced: false, DriveFollowingErrorWindowRaw: 10486, VelocityFeedforwardGain: 0, VelocityFeedbackRawPerUnitPerSecond: 1048576, VelocityOffsetRawPerUnitPerSecond: 1048576, PositionDemandRawPerUnit: 1048576, FollowingErrorRawPerUnit: 1048576, Homing: { Method: 35, AllowCurrentPositionZero: true } }, "field migration parameters");
        Check(axis.Runtime!.MotionCompletion == loaded.Project.Hardware.Axes.Single().Runtime!.MotionCompletion, "commissioned completion settings retained");
        Check(JsonSerializer.Serialize(migrated.Project.Tasks) == JsonSerializer.Serialize(loaded.Project.Tasks), "archived PLC scheduling policy retained");
        var runtime = EtherCatRuntimeCompiler.Compile(migrated.Project);
        Check(runtime.ReadyForRuntime, string.Join(";", runtime.Blockers));
        Check(runtime.TopologyFingerprint == EtherCatRuntimeCompiler.Compile(MigrationProfile.Create(loaded).Project).TopologyFingerprint, "configuration fingerprint reproducible");
        ContractChecks.Verify(migrated, runtime);
        var smoke = MigrationProfile.CreateSmoke(loaded);
        Check(smoke.Project.Id != migrated.Project.Id, "acceptance program has a separate identity");
        Check(JsonSerializer.Serialize(smoke.Project.Hardware) == JsonSerializer.Serialize(migrated.Project.Hardware), "acceptance does not expand production hardware limits");
        var smokeText = smoke.Sources.Single().Text;
        Check(smokeText.Contains("ForwardTurns : LREAL := 0.01;") && smokeText.Contains("ReverseTurns : LREAL := 0.01;") && smokeText.Contains("Speed : LREAL := 0.1;"), "bounded small-motion parameters");
        Check(smokeText.Contains("PowerEnable : BOOL := FALSE;") && smokeText.Contains("CycleExecute : BOOL := FALSE;"), "acceptance never starts motion on PLC RUN");
        foreach (var rpm in new[] { 300, 1000, 3000 })
        {
            var speedTest = MigrationProfile.CreateSpeedTest(loaded, rpm);
            var testAxis = speedTest.Project.Hardware.Axes.Single();
            Check(testAxis.MaximumVelocity == rpm / 60.0 && testAxis.MaximumJerk == 20, "commissioned speed/jerk only");
            Check(testAxis.MinPosition == (rpm == 300 ? -25 : rpm == 1000 ? -300 : -1000), "explicit test-only travel limit");
            Check(testAxis.Runtime!.MaximumFollowingError == .005 && testAxis.Runtime.FollowingErrorTimeoutMilliseconds == 20 && testAxis.Runtime.DriveFollowingErrorWindowRaw == 10486, "highspeed test keeps independent following protections");
            Check(speedTest.Project.Hardware.Runtime!.AxisTrace is { Enabled: true, Capacity: 120000 }, "bounded full-cycle wave recording enabled only in test project");
            var compiled = EtherCatRuntimeCompiler.Compile(speedTest.Project);
            Check(compiled.ReadyForRuntime, string.Join(";", compiled.Blockers));
            Check(compiled.MachineJson.Contains("workbench_axis_trace_v1", StringComparison.Ordinal), "trace capability required");
        }
        var temp = Path.Combine(Path.GetTempPath(), "runtime-migration-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(temp, "Migrated.stproj");
            await new ProjectStore().SaveAsync(path, migrated.Project, migrated.Sources);
            var reopened = await new ProjectStore().LoadAsync(path);
            Check(reopened.Project.Id == migrated.Project.Id && reopened.Sources.SequenceEqual(loaded.Sources), "desktop project save/reopen preserves exact archived ST");
            Check(EtherCatRuntimeCompiler.Compile(reopened.Project).MachineJson == runtime.MachineJson, "save/reopen retains migration machine contract");
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
        Console.WriteLine("PASS archive-based migration: source preservation, new reproducible identity, production bounds, motion/PDO/SDO/timing contract and desktop roundtrip");
    }
}

