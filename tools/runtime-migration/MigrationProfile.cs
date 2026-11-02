using StWorkbench.Core;

namespace StWorkbench.RuntimeMigration;

internal static class MigrationProfile
{
    public static LoadedProject CreateSpeedTest(LoadedProject source, int rpm)
    {
        if (rpm is not (300 or 1000 or 3000)) throw new ArgumentOutOfRangeException(nameof(rpm));
        var migrated = Create(source);
        var speed = rpm / 60.0;
        var distance = Math.Round((rpm == 3000 ? .36 : .9) * speed * speed, 6);
        var limit = rpm == 300 ? 25.0 : rpm == 1000 ? 300 : 1000;
        var acceleration = rpm == 3000 ? 5.0 : 2;
        var overspeed = rpm == 300 ? 7.5 : rpm == 1000 ? 20.0 : 55;
        migrated.Project.Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(migrated.Project.Id + ":formal-speed-acceptance-v1:" + rpm))[..16]);
        migrated.Project.Name = $"IS620N 正式ST空载验收 · {rpm}rpm";
        var axis = migrated.Project.Hardware.Axes.Single();
        migrated.Project.Hardware.Axes[0] = axis with { MinPosition = -limit, MaxPosition = limit, MaximumVelocity = speed,
            MaximumAcceleration = acceleration, MaximumDeceleration = acceleration, Runtime = axis.Runtime! with { MaximumActualVelocity = overspeed } };
        migrated.Project.Hardware.Runtime = migrated.Project.Hardware.Runtime! with { AxisTrace = new() { Enabled = true, Capacity = 120000 } };
        var text = migrated.Sources.Single().Text;
        foreach (var (before, after) in new[] {
            ("ForwardTurns : LREAL := 1.5;", $"ForwardTurns : LREAL := {distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};"),
            ("ReverseTurns : LREAL := 1.0;", $"ReverseTurns : LREAL := {distance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)};"),
            ("Speed : LREAL := 0.5; (* rev/s = 30 rpm *)", $"Speed : LREAL := {speed.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}; (* dedicated unloaded {rpm} rpm acceptance cap *)") })
        {
            if (!text.Contains(before, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected commissioned program defaults.");
            text = text.Replace(before, after, StringComparison.Ordinal);
        }
        return new(migrated.Project, [migrated.Sources.Single() with { Text = text }]);
    }
    public static LoadedProject CreateSmoke(LoadedProject source)
    {
        var migrated = Create(source);
        migrated.Project.Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(migrated.Project.Id + ":small-motion-acceptance-v1"))[..16]);
        migrated.Project.Name = "IS620N 正式ST链路 · 0.01圈往返验收";
        var text = migrated.Sources.Single().Text;
        foreach (var (before, after) in new[] {
            ("ForwardTurns : LREAL := 1.5;", "ForwardTurns : LREAL := 0.01;"),
            ("ReverseTurns : LREAL := 1.0;", "ReverseTurns : LREAL := 0.01;"),
            ("Speed : LREAL := 0.5; (* rev/s = 30 rpm *)", "Speed : LREAL := 0.1; (* rev/s cap = 6 rpm; short stroke may not reach cap *)") })
        {
            if (!text.Contains(before, StringComparison.Ordinal)) throw new InvalidDataException("Unexpected commissioned program defaults.");
            text = text.Replace(before, after, StringComparison.Ordinal);
        }
        return new(migrated.Project, [migrated.Sources.Single() with { Text = text }]);
    }
    public static LoadedProject Create(LoadedProject source)
    {
        var project = System.Text.Json.JsonSerializer.Deserialize<StProject>(System.Text.Json.JsonSerializer.Serialize(source.Project))!;
        // A reproducible derivative, preserving the original project and its ST.
        project.Id = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source.Project.Id + ":motion-migration-v1"))[..16]);
        project.Name += " · Runtime迁移";
        var axis = project.Hardware.Axes.Single();
        if (axis is not { Name: "X", ControllerAxis: "Axis1", Unit: "rev", Runtime.ProfileId: "is620n-csp" })
            throw new InvalidDataException("This migration profile is only for the commissioned X/Axis1 IS620N project.");
        project.Hardware.Axes[0] = axis with { MaximumJerk = 20, Runtime = axis.Runtime! with {
            DriveDemandProtection = true, DriveErrorOnly = true, DemandDeliveryEnforced = false,
            PositionDemandRawPerUnit = 1048576, FollowingErrorRawPerUnit = 1048576,
            VelocityFeedbackRawPerUnitPerSecond = 1048576, VelocityOffsetRawPerUnitPerSecond = 1048576,
            VelocityFeedforwardGain = 0, DriveFollowingErrorWindowRaw = 10486 } };
        project.Hardware.Runtime = project.Hardware.Runtime! with { CycleTiming = new() { Enabled = true } };
        var station = project.Hardware.Stations!.Single();
        project.Hardware.Stations![0] = station with { Runtime = station.Runtime! with { DcSync0ShiftNanoseconds = 670000 } };
        var device = project.Hardware.Devices.Single();
        var description = device.Description!;
        if (description.StartupCommands.Any(s => s.Index == 0x2005 && s.SubIndex == 0x14 || s.Index == 0x2008 && s.SubIndex == 0x14))
            throw new InvalidDataException("Existing feedforward startup commands need review.");
        project.Hardware.Devices[0] = device with { Description = description with { StartupCommands = description.StartupCommands.Concat(new[] {
            new EtherCatStartupCommand("PS", 0x2005, 0x14, "0100", false),
            new EtherCatStartupCommand("PS", 0x2008, 0x14, "E803", false) }).ToArray() } };
        // Preserve the archived task policy. PLC realtime scheduling is a
        // separate deployment with separate permission/capability verification.
        return new(project, source.Sources.ToArray());
    }
}
