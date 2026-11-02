using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

var root = Path.GetFullPath(args.Length == 0 ? "." : args[0]);
var directory = Path.Combine(root, "examples", "generic-ethercat");
var path = Path.Combine(directory, "IS620N-Generic.stproj");
if (File.Exists(path)) throw new InvalidOperationException("Example already exists; refusing to overwrite user changes.");
using var xml = File.OpenRead(Path.Combine(directory, "IS620N-commissioned.xml"));
var device = EsiImporter.Read(xml).Single();
var station = new EtherCatStation(Guid.NewGuid(), "IS620NDrive", device.Id, 0, device.Entries) { Runtime = new() { SelectedPdoIndices = [0x1600, 0x1A00], DcModeName = "DC1ms", ProfileId = "is620n-csp" } };
var axis = new AxisConfiguration("IS620N", "Axis1", "rev", -1, 1) { StationId = station.Id, MaximumVelocity = .01, MaximumAcceleration = .05, MaximumDeceleration = .05,
    Mechanics = new() { Kind = AxisKind.Rotary, FeedbackUnit = AxisFeedbackUnit.EncoderCounts, FeedbackCountsPerMotorRevolution = 1048576, MotorRevolutions = 1, LoadRevolutions = 1, TravelPerLoadRevolution = 1, HomeMode = AxisHomeMode.ControllerConfigured },
    Runtime = new() { ProfileId = "is620n-csp", MotorModel = "ISMH1-10B30CB-U230Z", MechanicsConfirmed = true, SafetyConfirmed = true, MaximumActualVelocity = .05, MaximumFollowingError = .005, FollowingErrorTimeoutMilliseconds = 20,
        Homing = new() { Method = 35, AllowCurrentPositionZero = true, SearchSpeedRaw = 1048, LatchSpeedRaw = 1048, AccelerationRaw = 10485, OffsetRaw = 0, TimeoutMilliseconds = 5000 },
        MotionCompletion = new() { PositionTolerance = .0001, VelocityTolerance = .0005, DwellMilliseconds = 100, PositionStabilityCompletion = true } } };
var item = new ProjectItem();
var project = new StProject { Name = "IS620N ESI 通用组态只读验证", Items = [item], Tasks = [new() { ProgramId = item.Id, IntervalMilliseconds = 10 }], Network = new("192.168.1.31", "a", "", "enp3s0"), Hardware = new() { Runtime = new(), Devices = [device], Stations = [station], Axes = [axis] } };
SourceDocument[] sources = [new(item.Id, item.SourcePath, await File.ReadAllTextAsync(Path.Combine(directory, "Main.st")))];
var configuration = EtherCatRuntimeCompiler.Compile(project);
if (!configuration.ReadyForRuntime) throw new InvalidDataException(string.Join("\n", configuration.Blockers));
await new ProjectStore().SaveAsync(path, project, sources);
var package = await new PackageBuilder(Path.Combine(root, "tools", "runtime-compat")).BuildAsync(ProjectValidator.Capture(project, sources), new Dictionary<Guid, string> { [item.Id] = "Main" }, "node", Path.Combine(root, "tools", "compiler-adapter", "compile.mjs"), Path.Combine(root, ".tools", "strucpp-0.7.0", "strucpp"), Path.Combine(root, "artifacts", "generic-ethercat-live"));
if (!package.Success) throw new InvalidOperationException(package.Compilation.Log);
var evidence = new { ProjectPath = path, package.BuildHash, package.PackagePath, configuration.TopologyFingerprint, Purpose = "Read-only ST; applying configuration is separate from PLC activation", FixtureOrigin = "Project reconstruction from previously recorded IS620N identity/PDO/DC; not vendor-authored ESI" };
await File.WriteAllTextAsync(Path.Combine(directory, "build-evidence.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(evidence));
