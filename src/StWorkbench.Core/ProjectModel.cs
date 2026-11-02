namespace StWorkbench.Core;

public enum ItemKind { Program, Function, FunctionBlock, DataType, GlobalVariables }
public sealed class ProjectItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Main";
    public ItemKind Kind { get; set; }
    public string SourcePath { get; set; } = "sources/Main.st";
}
public enum PlcTaskScheduling { Ordinary = 0, Realtime = 1 }
public sealed class PlcTask
{
    public string Name { get; set; } = "Cyclic";
    public Guid ProgramId { get; set; }
    public int IntervalMilliseconds { get; set; } = 10;
    public bool Enabled { get; set; } = true;
    public PlcTaskScheduling Scheduling { get; set; } = PlcTaskScheduling.Ordinary;
    public int Priority { get; set; } = 10;
    public int? Cpu { get; set; }
}
public sealed class HardwareConfiguration
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public EtherCatRuntimeSettings? Runtime { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<EtherCatStation>? Stations { get; set; }
    public List<EtherCatDevice> Devices { get; set; } = [];
    public List<IoMapping> Io { get; set; } = [];
    public List<AxisConfiguration> Axes { get; set; } = [];
}
public sealed record IoMapping(string Name, string Address, string Type, bool IsInput)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public Guid? StationId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public ushort? PdoIndex { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public byte? PdoSubIndex { get; init; }
}
public sealed record AxisConfiguration(string Name, string ControllerAxis, string Unit, double MinPosition, double MaxPosition)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public EtherCatAxisRuntimeSettings? Runtime { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AxisMechanics? Mechanics { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? StationId { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumVelocity { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumAcceleration { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumDeceleration { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public double? MaximumJerk { get; init; }
}
public sealed class StProject
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public CommunicationConfiguration? Communication { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public NetworkConfiguration? Network { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "ST 工程";
    public List<ProjectItem> Items { get; set; } = [];
    public List<PlcTask> Tasks { get; set; } = [];
    public HardwareConfiguration Hardware { get; set; } = new();
    public List<MotionPathAsset> Paths { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MotionCycleConfiguration? MotionCycle { get; set; }
}
public sealed record SourceDocument(Guid ItemId, string RelativePath, string Text);
public sealed record TaskSnapshot(string Name, Guid ProgramId, int IntervalMilliseconds, bool Enabled)
{
    public PlcTaskScheduling Scheduling { get; init; } = PlcTaskScheduling.Ordinary;
    public int Priority { get; init; } = 10;
    public int? Cpu { get; init; }
    public static TaskSnapshot From(PlcTask task) => new(task.Name, task.ProgramId, task.IntervalMilliseconds, task.Enabled)
    { Scheduling = task.Scheduling, Priority = task.Priority, Cpu = task.Cpu };
}
public sealed record BuildSnapshot(Guid ProjectId, string ProjectName, string SourceHash, string HardwareHash,
    IReadOnlyList<SourceDocument> Sources, IReadOnlyList<TaskSnapshot> Tasks)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? ProjectJson { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public CommunicationConfiguration? Communication { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] public string? HardwareConfigurationJson { get; init; }
    public IReadOnlyList<MotionPathAsset> Paths { get; init; } = [];
    public IReadOnlyList<AxisConfiguration> Axes { get; init; } = [];
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MotionCycleConfiguration? MotionCycle { get; init; }
}
public sealed record Diagnostic(string File, int Line, int Column, string Severity, string Message);
