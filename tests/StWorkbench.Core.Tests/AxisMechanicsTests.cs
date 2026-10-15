using StWorkbench.Core;
using System.Text.Json;

static class AxisMechanicsTests
{
    public static void Run()
    {
        Check(typeof(AxisConfiguration).GetProperty("Mechanics") is not null, "axis mechanics configuration is available");
        var axis = new AxisConfiguration("X", "Axis1", "mm", -1000, 1000);
        Check(!JsonSerializer.Serialize(axis).Contains("Mechanics"), "legacy null mechanics omitted");
        var linear = axis with { Mechanics = new() { MotorRevolutions = 10, FeedbackUnit = AxisFeedbackUnit.EncoderCounts } };
        Check(Math.Abs(AxisMechanicsConversion.CountsPerUnit(linear) - 131072d * 10 / 5) < 1e-9, "linear counts per mm");
        var rotary = linear with { Unit = "deg", Mechanics = linear.Mechanics! with { Kind = AxisKind.Rotary } };
        Check(Math.Abs(AxisMechanicsConversion.CountsPerUnit(rotary) - 131072d * 10 / 360) < 1e-9, "rotary counts per degree");
        Check(AxisMechanicsConversion.CountsPerUnit(rotary with { Unit = "rev" }) == 131072d * 10, "rotary counts per revolution");
        Check(AxisMechanicsConversion.CountsPerUnit(linear with { Mechanics = linear.Mechanics! with { ReverseDirection = true } }) > 0, "direction does not alter positive counts per unit");
        Check(AxisMechanicsValidator.Validate(linear).Count == 0 && AxisMechanicsValidator.Validate(rotary).Count == 0, "valid mechanical configurations");
        foreach (var invalid in new AxisMechanics[] {
            new() { Kind = (AxisKind)99 }, new() { FeedbackUnit = (AxisFeedbackUnit)99 }, new() { RotaryMode = (RotaryMode)99 },
            new() { HomeMode = (AxisHomeMode)99 }, new() { HomeDirection = (AxisHomeDirection)99 },
            new() { FeedbackCountsPerMotorRevolution = double.NaN }, new() { MotorRevolutions = double.PositiveInfinity },
            new() { LoadRevolutions = 0 }, new() { TravelPerLoadRevolution = -1 }, new() { ModuloStart = double.NaN },
            new() { ModuloPeriod = 0 }, new() { HomeOffset = double.NaN }, new() { HomeMode = AxisHomeMode.Switch, HomeOffset = 1001 },
            new() { HomeSearchVelocity = 0 }, new() { HomeLatchVelocity = 2 }, new() { RotaryMode = RotaryMode.Continuous }
        }) Check(AxisMechanicsValidator.Validate(axis with { Mechanics = invalid }).Count > 0, "invalid mechanics rejected: " + JsonSerializer.Serialize(invalid, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        Check(AxisMechanicsValidator.Validate(linear with { Unit = "deg" }).Count > 0, "linear unit mismatch");
        Check(AxisMechanicsValidator.Validate(rotary with { Unit = "mm" }).Count > 0, "rotary unit mismatch");
        Check(AxisMechanicsValidator.Validate(rotary with { Mechanics = rotary.Mechanics! with { RotaryMode = RotaryMode.Modulo, ModuloStart = 900 } }).Count > 0, "modulo interval outside soft bounds");
        Check(AxisMechanicsValidator.Validate(rotary with { Mechanics = rotary.Mechanics! with { RotaryMode = RotaryMode.Modulo, ModuloPeriod = 360 } }).Count == 0, "valid modulo interval");
        Check(AxisMechanicsValidator.Validate(linear with { MaximumVelocity = .5 }).Count == 0, "controller configured homing does not impose home velocity cap");
        Check(AxisMechanicsValidator.Validate(linear with { MinPosition = 100, MaxPosition = 200 }).Count == 0, "controller configured homing ignores unused default offset outside bounds");
        Check(AxisMechanicsValidator.Validate(linear with { MinPosition = 100, MaxPosition = 200, Mechanics = linear.Mechanics! with { HomeMode = AxisHomeMode.Switch } }).Count > 0, "explicit homing offset must be inside limited bounds");
        Check(AxisMechanicsValidator.Validate(linear with { MaximumVelocity = .5, Mechanics = linear.Mechanics! with { HomeMode = AxisHomeMode.Switch } }).Count > 0, "explicit homing respects velocity limit");
        var json = JsonSerializer.Serialize(rotary);
        Check(json.Contains("\"Rotary\"") && json.Contains("\"EncoderCounts\"") && JsonSerializer.Deserialize<AxisConfiguration>(json) == rotary, "string enum roundtrip");
        var main = new ProjectItem();
        var project = new StProject { Items = [main], Tasks = [new() { ProgramId = main.Id }] };
        SourceDocument[] docs = [new(main.Id, main.SourcePath, "PROGRAM Main\nEND_PROGRAM")];
        project.Hardware.Axes.Add(axis);
        var legacy = ProjectValidator.Capture(project, docs);
        Check(legacy.HardwareHash == ProjectValidator.Hash(JsonSerializer.Serialize(project.Hardware)), "legacy hardware identity unchanged");
        project.Hardware.Axes[0] = linear;
        var snapshot = ProjectValidator.Capture(project, docs);
        Check(snapshot.HardwareConfigurationJson is not null && snapshot.HardwareConfigurationJson.Contains("\"Mechanics\""), "explicit mechanics freezes hardware inventory without network");
        project.Hardware.Axes[0] = linear with { Mechanics = linear.Mechanics! with { MotorRevolutions = 20 } };
        Check(snapshot.Axes[0].Mechanics!.MotorRevolutions == 10 && snapshot.HardwareHash != ProjectValidator.Capture(project, docs).HardwareHash, "snapshot mechanics frozen and change affects identity");
        project.Hardware.Axes[0] = linear with { Unit = "deg" };
        Check(ProjectValidator.Validate(project, docs).Count > 0, "project validation includes mechanics");
        Console.WriteLine("PASS axis mechanics: conversion, validation, string enums, legacy identity and frozen snapshots");
    }
    static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
}
