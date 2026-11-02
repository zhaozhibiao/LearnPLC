using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class GenericEtherCatPackageSmoke
{
    public static async Task RunAsync(string adapter, string distribution)
    {
        var main = new ProjectItem();
        PdoEntry[] entries = [new(0x6040,0,16,"CW","UINT",false),new(0x607A,0,32,"Target","DINT",false),new(0x60FF,0,32,"Velocity","DINT",false),new(0x6060,0,8,"Mode","SINT",false),new(0x6041,0,16,"SW","UINT",true),new(0x6064,0,32,"Actual","DINT",true),new(0x606C,0,32,"ActualVelocity","DINT",true),new(0x6061,0,8,"ModeDisplay","SINT",true),new(0x603F,0,16,"Error","UINT",true)];
        var device = new EtherCatDevice(Guid.NewGuid(),"Servo",1,2,3,entries) { Description = new("source",[new(0x1600,"Rx",false,2,true,true,[],true,entries.Where(e => !e.IsInput).ToArray()),new(0x1A00,"Tx",true,3,true,true,[],true,entries.Where(e => e.IsInput).ToArray())],[new(2,"Outputs",null,null,null,true,true),new(3,"Inputs",null,null,null,true,false)],[],[],[]) };
        var station = new EtherCatStation(Guid.NewGuid(),"Drive",device.Id,0,entries) { Runtime = new() { ProfileId="cia402-csp" } };
        var runtime = new EtherCatAxisRuntimeSettings { MotorModel="Known motor",MechanicsConfirmed=true,SafetyConfirmed=true,MaximumActualVelocity=10,MaximumFollowingError=1,FollowingErrorTimeoutMilliseconds=20,Homing=new() { Method=1,SearchSpeedRaw=10,LatchSpeedRaw=5,AccelerationRaw=10,TimeoutMilliseconds=5000 },MotionCompletion=new() { PositionTolerance=.01,VelocityTolerance=.01,DwellMilliseconds=100 } };
        runtime = runtime with { VelocityFeedforwardGain = .75, DemandDeliveryEnforced = false };
        var axis = new AxisConfiguration("X","Axis1","mm",-10,10) { StationId=station.Id,Mechanics=new() { FeedbackUnit=AxisFeedbackUnit.EncoderCounts },MaximumVelocity=1,MaximumAcceleration=2,MaximumDeceleration=2,Runtime=runtime };
        var project = new StProject { Items=[main],Tasks=[new() { ProgramId=main.Id }],Hardware=new() { Runtime=new(),Devices=[device],Stations=[station],Axes=[axis] } };
        SourceDocument[] docs = [new(main.Id,main.SourcePath,"PROGRAM Main\nVAR Power : MC_Power; END_VAR\nPower(Axis := AXIS_X, Enable := FALSE);\nEND_PROGRAM\n")];
        var expected = EtherCatRuntimeCompiler.Compile(project);
        Check(expected.ReadyForRuntime,string.Join(";",expected.Blockers));
        var hardwareOnly = await new HardwarePackageBuilder().BuildAsync(project, Path.GetFullPath("artifacts/standalone-hardware-package"));
        Check(hardwareOnly.Success, string.Join(";", hardwareOnly.Errors));
        using (var hardwareZip = ZipFile.OpenRead(hardwareOnly.PackagePath))
        {
            Check(hardwareZip.GetEntry("generated.cpp") is null && hardwareZip.GetEntry("axis-manifest.json") is not null, "hardware has axes without ST compilation");
            using var hardwareManifest = JsonDocument.Parse(hardwareZip.GetEntry("build-manifest.json")!.Open());
            Check(hardwareManifest.RootElement.GetProperty("ArtifactKind").GetString() == "hardware-only", "standalone hardware kind");
            Check(hardwareManifest.RootElement.GetProperty("RequiredRuntimeCapabilities")[0].GetString() == "workbench_motion_migration_v1", "hardware declares migration capability");
            foreach (var member in hardwareZip.Entries.Where(e => e.FullName != "build-manifest.json"))
            {
                using var content = member.Open();
                Check(hardwareManifest.RootElement.GetProperty("Files").GetProperty(member.FullName).GetString() == Convert.ToHexString(await SHA256.HashDataAsync(content)), "standalone inventory " + member.FullName);
            }
        }
        static void VerifyHardwareIdentity(string packagePath, bool versioned)
        {
            using var archive = ZipFile.OpenRead(packagePath);
            using var manifest = JsonDocument.Parse(archive.GetEntry("build-manifest.json")!.Open());
            using var machine = JsonDocument.Parse(archive.GetEntry("machine-config.json")!.Open());
            var required = machine.RootElement.TryGetProperty("requiredRuntimeCapabilities", out var requirements) ? requirements.EnumerateArray().Select(e => e.GetString()).ToArray() : [];
            Check(manifest.RootElement.GetProperty("RequiredRuntimeCapabilities").EnumerateArray().Select(e => e.GetString()).SequenceEqual(required), "hardware capability manifest exactly mirrors machine");
            var inventory = JsonSerializer.Deserialize<SortedDictionary<string,string>>(manifest.RootElement.GetProperty("Files").GetRawText())!;
            var legacyHash = ProjectValidator.Hash(JsonSerializer.Serialize(new { ArtifactKind = "hardware-only", Files = inventory }));
            var actualHash = manifest.RootElement.GetProperty("BuildHash").GetString();
            if (!versioned) Check(actualHash == legacyHash && !manifest.RootElement.TryGetProperty("BuildRecipeVersion", out _), "zero/one capability hardware identity stays unchanged");
            else
            {
                Check(actualHash != legacyHash, "trace corrected manifest cannot reuse legacy files-only identity");
                Check(manifest.RootElement.GetProperty("BuildRecipeVersion").GetString() == "hardware-capabilities-v2", "trace recipe explicit");
                Check(actualHash == ProjectValidator.Hash(JsonSerializer.Serialize(new { ArtifactKind = "hardware-only", BuildRecipeVersion = "hardware-capabilities-v2", Files = inventory })), "trace identity includes explicit recipe discriminator");
            }
        }
        VerifyHardwareIdentity(hardwareOnly.PackagePath, false);
        var traceProject = JsonSerializer.Deserialize<StProject>(JsonSerializer.Serialize(project))!;
        var traceEntries = entries.Concat([new PdoEntry(0x6062,0,32,"Demand","DINT",true), new PdoEntry(0x60F4,0,32,"FollowingError","DINT",true)]).ToArray();
        traceProject.Hardware.Devices = [device with { Entries = traceEntries, Description = device.Description! with { PdoGroups = device.Description.PdoGroups.Select(g => g with { Entries = traceEntries.Where(e => e.IsInput == g.IsInput).ToArray() }).ToArray() } }];
        traceProject.Hardware.Stations = [station with { EnabledEntries = traceEntries }];
        traceProject.Hardware.Runtime = new() { AxisTrace = new() { Enabled = true, Capacity = 120000 } };
        var traceHardware = await new HardwarePackageBuilder().BuildAsync(traceProject, Path.GetFullPath("artifacts/standalone-hardware-package"));
        Check(traceHardware.Success, string.Join(";", traceHardware.Errors));
        VerifyHardwareIdentity(traceHardware.PackagePath, true);
        var ioOnlyProject = JsonSerializer.Deserialize<StProject>(JsonSerializer.Serialize(project))!;
        ioOnlyProject.Items.Clear(); ioOnlyProject.Tasks.Clear(); ioOnlyProject.Hardware.Axes.Clear();
        var ioOnly = await new HardwarePackageBuilder().BuildAsync(ioOnlyProject, Path.GetFullPath("artifacts/standalone-hardware-package"));
        Check(ioOnly.Success, "IO-only hardware with no ST objects/tasks: " + string.Join(";", ioOnly.Errors));
        using (var ioZip = ZipFile.OpenRead(ioOnly.PackagePath))
        using (var axisManifest = JsonDocument.Parse(ioZip.GetEntry("axis-manifest.json")!.Open()))
            Check(axisManifest.RootElement.GetProperty("Axes").GetArrayLength() == 0, "IO-only manifest represents no axes");
        VerifyHardwareIdentity(ioOnly.PackagePath, false);
        var snapshot = ProjectValidator.Capture(project,docs);
        project.Hardware.Runtime = new() { CycleTimeNanoseconds=2_000_000 };
        var builder = new PackageBuilder(); var names = new Dictionary<Guid,string> { [main.Id]="Main" }; var root = Path.GetFullPath("artifacts/generic-ethercat-package");
        var result = await builder.BuildAsync(snapshot,names,"node",adapter,distribution,root);
        Check(result.Success,result.Compilation.Log);
        using (var zip = ZipFile.OpenRead(result.PackagePath))
        {
            using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            Check(manifest.RootElement.GetProperty("RequiredRuntimeCapabilities")[0].GetString() == "workbench_motion_migration_v1", "program declares migration capability");
            Check(manifest.RootElement.GetProperty("EtherCatRuntimeInterfaceVersion").GetString()=="workbench-ethercat-v1","runtime interface marked");
            Check(manifest.RootElement.GetProperty("EtherCatTopologyFingerprint").GetString()==expected.TopologyFingerprint,"runtime fingerprint");
            foreach (var pair in new[] { ("ethercat-runtime.json",expected.TopologyJson),("machine-config.json",expected.MachineJson),("io-map.json",expected.IoMapJson) })
            {
                var entry = zip.GetEntry(pair.Item1) ?? throw new Exception("Missing " + pair.Item1);
                using var stream = entry.Open(); using var reader = new StreamReader(stream);
                Check(await reader.ReadToEndAsync()==pair.Item2,"exact frozen " + pair.Item1);
                using var bytes = entry.Open();
                Check(manifest.RootElement.GetProperty("Files").GetProperty(pair.Item1).GetString()==Convert.ToHexString(await SHA256.HashDataAsync(bytes)),"inventory " + pair.Item1);
            }
            using var basic = JsonDocument.Parse(zip.GetEntry("axis-manifest.json")!.Open());
            Check(!basic.RootElement.GetProperty("Axes")[0].TryGetProperty("Mechanics",out _),"generic basic axis omits delegated mechanics");
            using var hardware = JsonDocument.Parse(zip.GetEntry("hardware-configuration.json")!.Open());
            Check(hardware.RootElement.GetProperty("Hardware").GetProperty("Axes")[0].TryGetProperty("Mechanics",out _),"frozen hardware retains mechanics");
        }
        var changed = await builder.BuildAsync(ProjectValidator.Capture(project,docs),names,"node",adapter,distribution,root);
        Check(changed.Success && changed.BuildHash!=result.BuildHash,"runtime cycle changes build identity");
        project.Hardware.Stations![0] = station with { AliasAddress=1 };
        var blocked = await builder.BuildAsync(ProjectValidator.Capture(project,docs),names,"missing-node",adapter,distribution,root);
        Check(!blocked.Success && blocked.Compilation.Log.Contains("别名"),"runtime blockers returned before compiler starts");
        project.Hardware.Stations = null; project.Hardware.Axes.Clear(); project.Network = null;
        var empty = ProjectValidator.Capture(project,[docs[0] with { Text="PROGRAM Main\nEND_PROGRAM\n" }]);
        Check(empty.HardwareConfigurationJson is not null,"runtime opt in captured without stations");
        Console.WriteLine("PASS generic runtime package: exact files, inventory, fingerprint, frozen settings, mechanics delegation and blockers");
    }
    static void Check(bool value,string message) { if (!value) throw new Exception(message); }
}

