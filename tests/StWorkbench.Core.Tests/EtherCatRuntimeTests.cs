using System.Text;
using System.Text.Json;
using StWorkbench.Core;

static class EtherCatRuntimeTests
{
    public static void Run()
    {
        var project = new StProject();
        var legacy = new EtherCatDevice(Guid.NewGuid(), "Legacy", 1, 2, 3, []);
        project.Hardware.Devices.Add(legacy);
        project.Hardware.Stations = [new(Guid.NewGuid(), "Drive", legacy.Id, 0, [])];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "legacy metadata cannot be runtime-ready");
        const string xml = "<EtherCATInfo><Vendor><Id>1</Id></Vendor><Descriptions><Devices><Device><Type ProductCode='2' RevisionNo='3'>D</Type><Name>D</Name><Sm>MBoxOut</Sm><Sm>MBoxIn</Sm><Sm Watchdog='1'>Outputs</Sm><Sm>Inputs</Sm><RxPdo Sm='2' Fixed='1'><Index>#x1600</Index><Name>Out</Name><Entry><Index>#x7000</Index><SubIndex>1</SubIndex><BitLen>16</BitLen><DataType>UINT</DataType></Entry></RxPdo><TxPdo Sm='3'><Index>#x1A00</Index><Name>In</Name><Entry><Index>#x6000</Index><SubIndex>1</SubIndex><BitLen>16</BitLen><DataType>UINT</DataType></Entry></TxPdo></Device></Devices></Descriptions></EtherCATInfo>";
        var device = EsiImporter.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Single();
        project.Hardware.Runtime = new();
        project.Hardware.Devices = [device];
        var station = new EtherCatStation(Guid.NewGuid(), "IO", device.Id, 0, device.Entries);
        project.Hardware.Stations = [station];
        var result = EtherCatRuntimeCompiler.Compile(project);
        Check(result.ReadyForRuntime, string.Join(";", result.Blockers));
        using var topology = JsonDocument.Parse(result.TopologyJson);
        var slave = topology.RootElement.GetProperty("slaves")[0];
        Check(slave.GetProperty("position").GetInt32() == 0, "station position");
        Check(slave.GetProperty("pdos")[0].GetProperty("entries")[0].GetProperty("key").GetString() == "IO.0x7000:1", "stable PDO key");
        Check(result.TopologyJson == EtherCatRuntimeCompiler.Compile(project).TopologyJson, "deterministic topology");
        Check(topology.RootElement.GetProperty("machineSha256").GetString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(result.MachineJson))).ToLowerInvariant(), "machine exact UTF8 hash");
        project.Hardware.Io = [new("InputWord", "%IW0", "WORD", true) { StationId = station.Id, PdoIndex = 0x6000, PdoSubIndex = 1 }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "unsupported unsigned IO blocks runtime");
        project.Hardware.Io.Clear();
        var unsafeDevice = device with { Description = device.Description! with { StartupCommands = [new("PS",0x6040,0,"0f00",false)] } };
        project.Hardware.Devices = [unsafeDevice];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "startup enable controlword forbidden");
        project.Hardware.Devices = [device with { Description = device.Description! with { StartupCommands = [new("PS",0x60B1,0,"00000000",false)] } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "startup velocity-offset write forbidden");
        project.Hardware.Devices = [device with { Description = device.Description! with { StartupCommands = [new("SO",0x2000,0,"00",false)] } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "unsupported transition forbidden");
        project.Hardware.Devices = [device with { Description = device.Description! with { DcModes = [new("Sync",0x300,new(0,1,0),null)] } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "DC mode must be explicit");
        project.Hardware.Stations = [station with { Runtime = new() { DcModeName = "Sync" } }];
        Check(EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "DC factor resolves configured cycle");
        project.Hardware.Devices = [device];
        project.Hardware.Stations = [station with { AliasAddress = 1 }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "unsupported alias blocks runtime");
        project.Hardware.Stations = [station with { EnabledEntries = [device.Entries[0]] }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "partial group is rejected");
        project.Hardware.Stations = [station with { Runtime = new() { SelectedPdoIndices = [0x1600, 0xFFFF] } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "unknown PDO selection blocks runtime");
        project.Hardware.Stations = [station];
        project.Hardware.Runtime = null;
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "explicit global runtime opt in required");
        project.Hardware.Runtime = new();
        var intEntries = device.Entries.Select(e => e with { Type = "INT" }).ToArray();
        device = device with { Entries = intEntries, Description = device.Description! with { PdoGroups = device.Description.PdoGroups.Select(g => g with { Entries = g.Entries.Select(e => e with { Type = "INT" }).ToArray() }).ToArray() } };
        project.Hardware.Devices = [device]; station = station with { EnabledEntries = intEntries }; project.Hardware.Stations = [station];
        project.Hardware.Io = [new("Input", "%IW0", "INT", true) { StationId = station.Id, PdoIndex = 0x6000, PdoSubIndex = 1 }];
        var withIo = EtherCatRuntimeCompiler.Compile(project);
        Check(withIo.ReadyForRuntime, string.Join(";", withIo.Blockers));
        using var io = JsonDocument.Parse(withIo.IoMapJson);
        Check(io.RootElement.GetProperty("groups")[0].GetProperty("encoding").GetString() == "Int16", "native IO scalar encoding");
        using var canonical = JsonDocument.Parse("{\"z\":[{\"b\":2,\"a\":1}],\"a\":\"输入\"}");
        Check(EtherCatRuntimeCompiler.CanonicalJson(canonical.RootElement) == "{\"a\":\"输入\",\"z\":[{\"a\":1,\"b\":2}]}", "canonical cross language ordering");
        AxisProfiles();
        Console.WriteLine("PASS: generic EtherCAT runtime compiler guards and deterministic topology");
    }
    static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }

    static void MigrationConfiguration(StProject project, AxisConfiguration axis, EtherCatAxisRuntimeSettings runtime, EtherCatStation station, EtherCatDevice device)
    {
        var originalAxes = project.Hardware.Axes;
        var originalStations = project.Hardware.Stations;
        var originalGlobal = project.Hardware.Runtime;
        Check(!JsonSerializer.Serialize(new EtherCatRuntimeSettings()).Contains("AxisTrace"), "legacy runtime omits trace settings");
        var beforeTrace = EtherCatRuntimeCompiler.Compile(project);
        foreach (var capacity in new[] { 1, 120000 })
        {
            project.Hardware.Runtime = originalGlobal! with { AxisTrace = new() { Enabled = true, Capacity = capacity } };
            var traced = EtherCatRuntimeCompiler.Compile(project);
            Check(traced.ReadyForRuntime, string.Join(";", traced.Blockers));
            using var tracedMachine = JsonDocument.Parse(traced.MachineJson);
            Check(tracedMachine.RootElement.GetProperty("axisTrace").GetProperty("capacity").GetInt32() == capacity, "explicit bounded trace capacity reaches machine");
            Check(tracedMachine.RootElement.GetProperty("requiredRuntimeCapabilities").EnumerateArray().Any(c => c.GetString() == "workbench_axis_trace_v1"), "trace requires native trace capability");
            Check(EtherCatRuntimeCompiler.RequiresMotionMigration(project), "trace requires migration capability");
            Check(beforeTrace.TopologyFingerprint != traced.TopologyFingerprint, "trace changes immutable configuration identity");
        }
        foreach (var capacity in new[] { 0, -1, 120001 })
        {
            project.Hardware.Runtime = originalGlobal! with { AxisTrace = new() { Enabled = true, Capacity = capacity } };
            Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "invalid trace capacity rejected");
        }
        project.Hardware.Runtime = originalGlobal! with { AxisTrace = new() { Enabled = true, Capacity = 1 } };
        project.Hardware.Axes = [];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "trace requires one axis");
        project.Hardware.Axes = [originalAxes[0], originalAxes[0] with { Name = "OtherTraceAxis" }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "trace rejects multiple axes");
        project.Hardware.Axes = originalAxes;
        project.Hardware.Runtime = originalGlobal;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var configured = JsonSerializer.Deserialize<EtherCatAxisRuntimeSettings>(JsonSerializer.Serialize(originalAxes[0].Runtime)[..^1] + ",\"DriveDemandProtection\":true,\"DriveErrorOnly\":true,\"DemandDeliveryEnforced\":false,\"DemandDeliveryTolerance\":0.02,\"VelocityFeedbackRawPerUnitPerSecond\":123,\"VelocityOffsetRawPerUnitPerSecond\":456,\"VelocityFeedforwardGain\":0.5,\"FollowingErrorRawPerUnit\":100,\"PositionDemandRawPerUnit\":100,\"DriveFollowingErrorWindowRaw\":321}", options)!;
        project.Hardware.Axes = [originalAxes[0] with { MaximumJerk = 15, Runtime = configured }];
        var compiled = EtherCatRuntimeCompiler.Compile(project);
        Check(compiled.ReadyForRuntime, string.Join(";", compiled.Blockers));
        using (var json = JsonDocument.Parse(compiled.MachineJson))
        {
            var output = json.RootElement.GetProperty("axes")[0];
            Check(output.TryGetProperty("maximumJerk", out var jerk) && jerk.GetDouble() == 15, "configured jerk must reach motion core");
            Check(output.GetProperty("driveDemandProtection").GetBoolean() && output.GetProperty("driveErrorOnly").GetBoolean(), "auxiliary protection mode roundtrip");
            Check(!output.GetProperty("demandDeliveryEnforced").GetBoolean(), "delivery diagnostics can remain nonenforcing");
            Check(output.GetProperty("velocityFeedbackRawPerUnitPerSecond").GetDouble() == 123 && output.GetProperty("velocityOffsetRawPerUnitPerSecond").GetDouble() == 456, "separate feedback and feedforward velocity scaling");
            Check(output.GetProperty("pdoEntries").GetProperty("positionDemand").GetString()!.EndsWith("0x6062:0") && output.GetProperty("pdoEntries").GetProperty("followingError").GetString()!.EndsWith("0x60F4:0"), "protection binds explicit demand and error PDOs");
        }
        using (var json = JsonDocument.Parse(compiled.TopologyJson))
        {
            var sd = json.RootElement.GetProperty("slaves")[0].GetProperty("startupSdos").EnumerateArray().Single(s => s.GetProperty("index").GetInt32() == 0x6065);
            Check(sd.GetProperty("data")[0].GetInt32() == 65 && sd.GetProperty("data")[1].GetInt32() == 1, "6065 window is little endian engineering value");
        }
        Check(compiled.TopologyFingerprint != EtherCatRuntimeCompiler.Compile(new StProject { Hardware = new() { Runtime = originalGlobal, Devices = project.Hardware.Devices, Stations = originalStations, Axes = originalAxes } }).TopologyFingerprint, "new protection configuration changes package identity");
        using (var machine = JsonDocument.Parse(compiled.MachineJson)) Check(machine.RootElement.GetProperty("requiredRuntimeCapabilities")[0].GetString() == "workbench_motion_migration_v1", "new semantics require new runtime capability");
        var legacyJson = JsonSerializer.Serialize(new EtherCatAxisRuntimeSettings());
        Check(!legacyJson.Contains("DriveDemandProtection") && !legacyJson.Contains("DriveFollowingErrorWindowRaw"), "omitted migration fields preserve legacy serialization");
        var dcDevice = project.Hardware.Devices[0];
        project.Hardware.Devices = [dcDevice with { Description = dcDevice.Description! with { DcModes = [new("Sync", 0x300, new(0,1,0), null)] } }];
        project.Hardware.Stations = [originalStations![0] with { Runtime = JsonSerializer.Deserialize<EtherCatStationRuntimeSettings>("{\"ProfileId\":\"is620n-csp\",\"DcModeName\":\"Sync\",\"DcSync0ShiftNanoseconds\":670000}") }];
        project.Hardware.Runtime = JsonSerializer.Deserialize<EtherCatRuntimeSettings>("{\"CycleTiming\":{\"Enabled\":true,\"Model\":\"R2M\",\"BoundaryGuardNs\":200000,\"RemainingSendBudgetNs\":100000,\"RequiredGoodCycles\":1000}}");
        var timed = EtherCatRuntimeCompiler.Compile(project);
        Check(timed.ReadyForRuntime, string.Join(";", timed.Blockers));
        using (var machine = JsonDocument.Parse(timed.MachineJson)) Check(machine.RootElement.GetProperty("cycleTiming").GetProperty("boundaryGuardNs").GetInt32() == 200000, "configured timing reaches machine");
        using (var topology = JsonDocument.Parse(timed.TopologyJson)) Check(topology.RootElement.GetProperty("slaves")[0].GetProperty("dc").GetProperty("sync0ShiftNs").GetInt32() == 670000, "explicit station DC offset override");
        project.Hardware.Stations = [project.Hardware.Stations![0] with { Runtime = project.Hardware.Stations![0].Runtime! with { DcSync0ShiftNanoseconds = 1000000 } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "DC phase outside timing budget rejected");
        project.Hardware.Stations = [project.Hardware.Stations![0] with { Runtime = project.Hardware.Stations![0].Runtime! with { DcSync0ShiftNanoseconds = 0 } }];
        Check(EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "R2M zero DC phase is valid and not a global bench phase");
        project.Hardware.Runtime = project.Hardware.Runtime! with { CycleTiming = project.Hardware.Runtime!.CycleTiming! with { BoundaryGuardNs = 450000, RemainingSendBudgetNs = 200000 } };
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "R2M symmetric boundary guards leave insufficient send budget");
        project.Hardware.Stations = originalStations; project.Hardware.Runtime = originalGlobal;
        project.Hardware.Devices = [dcDevice];
        foreach (var bad in new[] { configured with { FollowingErrorRawPerUnit = 0 }, configured with { DriveDemandProtection = false, DriveErrorOnly = true }, configured with { VelocityFeedforwardGain = -1 }, configured with { DemandDeliveryTolerance = -1 }, configured with { PositionDemandRawPerUnit = double.MaxValue }, configured with { FollowingErrorRawPerUnit = double.MaxValue }, configured with { VelocityFeedbackRawPerUnitPerSecond = double.MaxValue }, configured with { VelocityOffsetRawPerUnitPerSecond = double.MaxValue } })
        {
            project.Hardware.Axes = [originalAxes[0] with { Runtime = bad }];
            Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "invalid migration parameters rejected");
        }
        project.Hardware.Axes = [originalAxes[0] with { Runtime = configured with { VelocityFeedforwardGain = 1.5 } }];
        var excessiveFeedforward = EtherCatRuntimeCompiler.Compile(project);
        Check(!excessiveFeedforward.ReadyForRuntime && excessiveFeedforward.Blockers.Any(b => b.Contains("前馈增益")), "feedforward gain above one rejected before native load");
        foreach (var boundaryGain in new[] { 0.0, 1.0 })
        {
            project.Hardware.Axes = [originalAxes[0] with { Runtime = configured with { VelocityFeedforwardGain = boundaryGain } }];
            Check(EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "feedforward gain accepts inclusive zero/one boundary");
        }
        project.Hardware.Axes = [originalAxes[0] with { Runtime = configured }];
        project.Hardware.Devices = [dcDevice with { Description = dcDevice.Description! with { StartupCommands = [new("PS", 0x6065, 0, "41010000", false)] } }];
        var conflictingWindow = EtherCatRuntimeCompiler.Compile(project);
        Check(!conflictingWindow.ReadyForRuntime && conflictingWindow.Blockers.Any(b => b.Contains("6065") && b.Contains("冲突")), "6065 engineering window conflicts with ESI startup window even if values match");
        ProtectionPdoGuards(project, axis, runtime, configured, station, device);
        Console.WriteLine("PASS migration review: feedforward [0,1], duplicate 6065, both auxiliary PDO direction/type/width guards");
        // Generic ESI without 6062/60F4 must never turn the selected monitor into zero error.
        project.Hardware.Devices = [device]; project.Hardware.Stations = [station];
        project.Hardware.Axes = [axis with { Runtime = configured with { ProfileId = "cia402-csp", Homing = runtime.Homing } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "missing protection PDOs fail closed");
        project.Hardware.Devices = [device with { VendorId=0x00100000,ProductCode=0x000C0108,Revision=0x00010001 }];
        project.Hardware.Stations = originalStations; project.Hardware.Axes = originalAxes; project.Hardware.Runtime = originalGlobal;
    }
    static void ProtectionPdoGuards(StProject project, AxisConfiguration axis, EtherCatAxisRuntimeSettings runtime, EtherCatAxisRuntimeSettings configured, EtherCatStation station, EtherCatDevice device)
    {
        var genericRuntime = configured with { ProfileId = "cia402-csp", Homing = runtime.Homing, DriveFollowingErrorWindowRaw = null };
        project.Hardware.Axes = [axis with { Runtime = genericRuntime }];
        PdoEntry[] validEntries = [..device.Entries, new(0x6062, 0, 32, "Demand", "DINT", true), new(0x60F4, 0, 32, "Error", "DINT", true)];
        void SetEntries(PdoEntry[] entries)
        {
            project.Hardware.Devices = [device with { Entries = entries, Description = device.Description! with { PdoGroups = device.Description.PdoGroups.Select(g => g with { Entries = entries.Where(e => e.IsInput == g.IsInput).ToArray() }).ToArray() } }];
            project.Hardware.Stations = [station with { EnabledEntries = entries }];
        }
        SetEntries(validEntries);
        var originalTraceGlobal = project.Hardware.Runtime;
        project.Hardware.Runtime = originalTraceGlobal! with { AxisTrace = new() { Enabled = true, Capacity = 100 } };
        project.Hardware.Axes = [axis with { Runtime = genericRuntime with { DriveDemandProtection = false, DriveErrorOnly = false } }];
        Check(EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime, "trace PDO baseline independent of auxiliary protection");
        foreach (var traceIndex in new ushort[] { 0x6062, 0x60F4 })
        {
            foreach (var corruption in new[] { "missing", "direction", "type", "width" })
            {
                var traceEntries = validEntries.Where(e => corruption != "missing" || e.Index != traceIndex).Select(e => e.Index != traceIndex ? e : corruption switch {
                    "direction" => e with { IsInput = false }, "type" => e with { Type = "UDINT" },
                    "width" => e with { BitLength = 16 }, _ => e }).ToArray();
                SetEntries(traceEntries);
                var traceRejected = EtherCatRuntimeCompiler.Compile(project);
                Check(!traceRejected.ReadyForRuntime && traceRejected.Blockers.Any(b => b.Contains($"0x{traceIndex:X4}:0")), "trace PDO rejects " + corruption + " independently of protection");
            }
        }
        project.Hardware.Runtime = originalTraceGlobal;
        project.Hardware.Axes = [axis with { Runtime = genericRuntime }];
        SetEntries(validEntries);
        var baseline = EtherCatRuntimeCompiler.Compile(project);
        Check(baseline.ReadyForRuntime, "generic auxiliary PDO baseline: " + string.Join(";", baseline.Blockers));
        foreach (var index in new ushort[] { 0x6062, 0x60F4 })
        {
            foreach (var corruption in new[] { "direction", "type", "width" })
            {
                var invalidEntries = validEntries.Select(e => e.Index != index ? e : corruption switch {
                    "direction" => e with { IsInput = false },
                    "type" => e with { Type = "UDINT" },
                    _ => e with { BitLength = 16 }
                }).ToArray();
                SetEntries(invalidEntries);
                var rejected = EtherCatRuntimeCompiler.Compile(project);
                Check(!rejected.ReadyForRuntime && rejected.Blockers.Any(b => b.Contains($"0x{index:X4}:0") && b.Contains("32 位有符号输入")), $"auxiliary PDO 0x{index:X4} rejects wrong {corruption}");
            }
        }
    }
    static void AxisProfiles()
    {
        PdoEntry[] entries = [new(0x6040,0,16,"CW","UINT",false),new(0x607A,0,32,"Target","DINT",false),new(0x60FF,0,32,"Velocity","DINT",false),new(0x6060,0,8,"Mode","SINT",false),new(0x6041,0,16,"SW","UINT",true),new(0x6064,0,32,"Actual","DINT",true),new(0x606C,0,32,"ActualVelocity","DINT",true),new(0x6061,0,8,"ModeDisplay","SINT",true),new(0x603F,0,16,"Error","UINT",true)];
        var device = new EtherCatDevice(Guid.NewGuid(),"Servo",1,2,3,entries) { Description = new("abc",[new(0x1600,"Rx",false,2,true,true,[],true,entries.Where(e => !e.IsInput).ToArray()),new(0x1A00,"Tx",true,3,true,true,[],true,entries.Where(e => e.IsInput).ToArray())],[new(2,"Outputs",null,null,null,true,true),new(3,"Inputs",null,null,null,true,false)],[],[],[]) };
        var station = new EtherCatStation(Guid.NewGuid(),"Drive",device.Id,0,entries) { Runtime = new() { ProfileId = "cia402-csp" } };
        var runtime = new EtherCatAxisRuntimeSettings { MotorModel="Known motor", MechanicsConfirmed=true,SafetyConfirmed=true, MaximumActualVelocity=10,MaximumFollowingError=1,FollowingErrorTimeoutMilliseconds=20,Homing=new() { Method=1,SearchSpeedRaw=10,LatchSpeedRaw=5,AccelerationRaw=10,TimeoutMilliseconds=5000 },MotionCompletion=new() { PositionTolerance=.01,VelocityTolerance=.01,DwellMilliseconds=100 } };
        var axis = new AxisConfiguration("Axis1","Axis1","mm",-10,10) { StationId=station.Id, Mechanics=new() { FeedbackUnit=AxisFeedbackUnit.EncoderCounts },MaximumVelocity=1,MaximumAcceleration=2,MaximumDeceleration=2,Runtime=runtime };
        var project = new StProject { Hardware = new() { Runtime=new(),Devices=[device],Stations=[station],Axes=[axis] } };
        var good = EtherCatRuntimeCompiler.Compile(project);
        Check(good.ReadyForRuntime,string.Join(";",good.Blockers));
        using (var genericMachine = JsonDocument.Parse(good.MachineJson))
        {
            var pdo = genericMachine.RootElement.GetProperty("axes")[0].GetProperty("pdoEntries");
            Check(pdo.TryGetProperty("targetVelocity", out _) && !pdo.TryGetProperty("velocityOffset", out _), "generic CSP keeps the standard target velocity map");
        }
        foreach (var mode in new[] { RotaryMode.Modulo, RotaryMode.Continuous })
        {
            project.Hardware.Axes = [axis with { Unit="deg",MinPosition=0,MaxPosition=360,Mechanics=axis.Mechanics! with { Kind=AxisKind.Rotary,RotaryMode=mode,ModuloStart=0,ModuloPeriod=360 } }];
            Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"native runtime cannot silently ignore " + mode);
        }
        project.Hardware.Axes = [axis with { Mechanics=axis.Mechanics! with { HomeMode=AxisHomeMode.Switch,HomeDirection=AxisHomeDirection.Positive,HomeOffset=2,HomeSearchVelocity=.8,HomeLatchVelocity=.2 } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"legacy mechanical home settings cannot be silently ignored");
        project.Hardware.Axes = [axis with { Runtime = runtime with { VelocityUnits="Rpm" } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"rpm adapter not supported");
        project.Hardware.Axes = [axis with { Runtime = runtime with { Homing = runtime.Homing! with { Method=35,AllowCurrentPositionZero=true } } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"generic current position zero rejected");
        project.Hardware.Stations = [station with { Runtime = new() { ProfileId="is620n-csp" } }];
        project.Hardware.Axes = [axis with { Runtime = runtime with { ProfileId="is620n-csp",Homing = runtime.Homing! with { Method=35,AllowCurrentPositionZero=true } } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"IS620N-specific zero requires known drive identity");
        project.Hardware.Devices = [device with { VendorId=0x00100000,ProductCode=0x000C0108,Revision=0x00010001 }];
        var inovance = EtherCatRuntimeCompiler.Compile(project);
        Check(inovance.ReadyForRuntime,"known IS620N explicit zero accepted");
        MigrationConfiguration(project, axis, runtime, station, device);
        using (var is620nMachine = JsonDocument.Parse(inovance.MachineJson))
        {
            var pdo = is620nMachine.RootElement.GetProperty("axes")[0].GetProperty("pdoEntries");
            Check(pdo.TryGetProperty("velocityOffset", out _) && !pdo.TryGetProperty("targetVelocity", out _), "IS620N CSP maps its feedforward channel to velocity offset 0x60B1");
        }
        using (var is620nTopology = JsonDocument.Parse(inovance.TopologyJson))
        {
            var rx = is620nTopology.RootElement.GetProperty("slaves")[0].GetProperty("pdos").EnumerateArray()
                .Single(pdo => pdo.GetProperty("direction").GetString() == "Rx");
            var objects = rx.GetProperty("entries").EnumerateArray().Select(entry => entry.GetProperty("index").GetUInt16()).ToArray();
            Check(objects.Contains((ushort)0x60B1) && !objects.Contains((ushort)0x60FF), "IS620N profile builds its evidenced custom 0x1600 map from the ESI PDO group");
        }
        project.Hardware.Devices = [device]; project.Hardware.Stations = [station];
        project.Hardware.Axes = [axis with { Runtime = runtime with { MechanicsConfirmed=false } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"unconfirmed mechanics rejected");
        project.Hardware.Axes = [axis with { Runtime = runtime with { MotorModel="CONFIGURE_BEFORE_ENABLE" } }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"native placeholder motor rejected");
        project.Hardware.Axes = [axis with { MaximumVelocity=double.NaN }];
        Check(!EtherCatRuntimeCompiler.Compile(project).ReadyForRuntime,"invalid numeric axis returns blocker");
    }
}


