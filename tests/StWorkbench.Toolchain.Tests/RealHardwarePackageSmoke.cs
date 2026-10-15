using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class RealHardwarePackageSmoke
{
    public static async Task RunAsync(string adapter, string distribution)
    {
        var main = new ProjectItem();
        var project = new StProject { Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        SourceDocument[] docs = [new(main.Id, main.SourcePath, "PROGRAM Main\nEND_PROGRAM\n")];
        var names = new Dictionary<Guid, string> { [main.Id] = "Main" };
        var builder = new PackageBuilder();
        var root = Path.GetFullPath("artifacts/real-hardware-package");
        var legacy = await builder.BuildAsync(ProjectValidator.Capture(project, docs), names, "node", adapter, distribution, root);
        Check(legacy.Success, legacy.Compilation.Log);
        using (var zip = ZipFile.OpenRead(legacy.PackagePath)) Check(zip.GetEntry("hardware-configuration.json") is null, "legacy candidate unchanged");
        project.Network = new("plc.local", "plc", "", "eth0");
        PdoEntry[] entries = [new(0x6000, 1, 16, "Input", "UINT", true)];
        var device = new EtherCatDevice(Guid.NewGuid(), "Catalog", 1, 2, 3, entries);
        project.Hardware.Devices.Add(device);
        var station = new EtherCatStation(Guid.NewGuid(), "Station1", device.Id, 0, entries);
        project.Hardware.Stations = [station];
        project.Hardware.Io.Add(new("Input", "%IW0", "WORD", true) { StationId = station.Id, PdoIndex = 0x6000, PdoSubIndex = 1 });
        project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100) { StationId = station.Id });
        var snapshot = ProjectValidator.Capture(project, docs);
        project.Hardware.Stations.Clear(); project.Network = project.Network with { EtherCatInterface = "eth1" };
        var candidate = await builder.BuildAsync(snapshot, names, "node", adapter, distribution, root);
        Check(candidate.Success, candidate.Compilation.Log);
        using (var zip = ZipFile.OpenRead(candidate.PackagePath))
        {
            var entry = zip.GetEntry("hardware-configuration.json") ?? throw new Exception("Missing frozen hardware configuration");
            using var stream = entry.Open(); using var reader = new StreamReader(stream);
            Check(await reader.ReadToEndAsync() == snapshot.HardwareConfigurationJson, "package matches snapshot");
            using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
            using var bytes = entry.Open();
            Check(manifest.RootElement.GetProperty("Files").GetProperty(entry.FullName).GetString() == Convert.ToHexString(await SHA256.HashDataAsync(bytes)), "inventory covers hardware configuration");
        }
        var tampered = await builder.BuildAsync(snapshot with { HardwareConfigurationJson = snapshot.HardwareConfigurationJson!.Replace("eth0", "eth1") }, names, "node", adapter, distribution, root);
        Check(!tampered.Success, "tampered snapshot rejected");
        var invalidJson = "{invalid";
        var invalid = await builder.BuildAsync(snapshot with { HardwareConfigurationJson = invalidJson, HardwareHash = ProjectValidator.Hash(invalidJson) }, names, "node", adapter, distribution, root);
        Check(!invalid.Success, "invalid hardware JSON rejected");
        var node = System.Text.Json.Nodes.JsonNode.Parse(snapshot.HardwareConfigurationJson!)!;
        node["Hardware"]!["Stations"]![0]!["DeviceId"] = Guid.NewGuid();
        var danglingJson = node.ToJsonString();
        var dangling = await builder.BuildAsync(snapshot with { HardwareConfigurationJson = danglingJson, HardwareHash = ProjectValidator.Hash(danglingJson) }, names, "node", adapter, distribution, root);
        Check(!dangling.Success, "invalid topology with recomputed hash rejected");
        var axisTamper = await builder.BuildAsync(snapshot with { Axes = [snapshot.Axes[0] with { StationId = Guid.NewGuid() }] }, names, "node", adapter, distribution, root);
        Check(!axisTamper.Success, "snapshot axis metadata divergence rejected");
        Console.WriteLine("PASS real hardware packaging, legacy omission, inventory and tamper rejection");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}

