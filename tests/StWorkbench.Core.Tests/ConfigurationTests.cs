using System.Text.Json;
using System.Text.Json.Nodes;
using StWorkbench.Core;

public static class ConfigurationTests
{
    public static async Task Run()
    {
        var main = new ProjectItem();
        var project = new StProject { Items = [main], Tasks = [new PlcTask { ProgramId = main.Id }] };
        SourceDocument[] docs = [new(main.Id, main.SourcePath, "PROGRAM Main\nEND_PROGRAM\n")];
        project.Hardware.Io.Add(new("Input", "%IW0", "WORD", true));
        project.Hardware.Axes.Add(new("X", "Axis1", "mm", -100, 100));
        var oldJson = JsonSerializer.Serialize(project);
        var legacy = ProjectValidator.Capture(project, docs);
        Check(legacy.HardwareHash == ProjectValidator.Hash("""{"Devices":[],"Io":[{"Name":"Input","Address":"%IW0","Type":"WORD","IsInput":true}],"Axes":[{"Name":"X","ControllerAxis":"Axis1","Unit":"mm","MinPosition":-100,"MaxPosition":100}]}"""), "legacy hardware hash unchanged byte for byte");
        Check(!oldJson.Contains("Network") && !oldJson.Contains("Stations") && !oldJson.Contains("StationId") && !oldJson.Contains("PdoIndex"), "null fields absent from legacy JSON");
        Check(legacy.HardwareConfigurationJson is null, "legacy snapshot has no new metadata");
        project.Network = new("plc.local", "plc", "https://localhost:8443", "eth0");
        Check(ProjectValidator.Capture(project, docs).HardwareHash != legacy.HardwareHash, "network changes hardware identity");
        var network = project.Network;
        foreach (var host in new[] { "localhost", "192.168.1.10", "2001:db8::1", "plc-1.factory.local" })
        { project.Network = network with { ManagementHost = host }; Valid(); }
        foreach (var host in new[] { "", "-plc", "plc;echo", "plc/x", "plc:22", "user@plc", "999.1.2.3", "plc..local", "plc\n", "fe80::1%eth0" })
        { project.Network = network with { ManagementHost = host }; Invalid("host"); }
        foreach (var user in new[] { "", "-option", "plc;id", "plc user", "plc\n", "$(id)" })
        { project.Network = network with { SshUser = user }; Invalid("SSH username"); }
        foreach (var endpoint in new[] { "http://plc", "https://user:pass@plc", "https://plc/path", "https://plc?x", "https://plc/#x", "https://plc\\x", " https://plc", "https://plc ", "https://plc/a/../", "https://plc/%2e%2e/" })
        { project.Network = network with { RuntimeEndpoint = endpoint }; Invalid("HTTPS origin"); }
        foreach (var name in new[] { "abcdefghijklmnop", "eth0;id", "eth/0", "eth 0", "网卡", ".", ".." })
        { project.Network = network with { EtherCatInterface = name }; Invalid("interface"); }
        project.Network = network with { RuntimeEndpoint = "", EtherCatInterface = "" }; Valid();
        project.Network = network;
        PdoEntry[] entries = [new(0x6000, 1, 16, "Input", "UINT", true), new(0x7000, 1, 16, "Output", "UINT", false)];
        var device = new EtherCatDevice(Guid.NewGuid(), "Catalog", 1, 2, 3, entries);
        project.Hardware.Devices.Add(device);
        var station = new EtherCatStation(Guid.NewGuid(), "Station1", device.Id, 0, entries);
        project.Hardware.Stations = [station, station with { Id = Guid.NewGuid(), Name = "Station2" }]; Valid();
        project.Hardware.Io[0] = project.Hardware.Io[0] with { StationId = station.Id, PdoIndex = 0x6000, PdoSubIndex = 1 };
        project.Hardware.Axes[0] = project.Hardware.Axes[0] with { StationId = station.Id }; Valid();
        var linked = project.Hardware.Io[0];
        foreach (var io in new[] { linked with { StationId = null }, linked with { PdoIndex = null }, linked with { PdoSubIndex = null }, linked with { StationId = Guid.NewGuid() }, linked with { PdoIndex = 0x7000 }, linked with { Type = "INT" }, linked with { Type = "DWORD" } })
        { project.Hardware.Io[0] = io; Invalid("IO station/PDO link"); }
        project.Hardware.Io[0] = linked;
        var axis = project.Hardware.Axes[0]; project.Hardware.Axes[0] = axis with { StationId = Guid.NewGuid() }; Invalid("axis station link"); project.Hardware.Axes[0] = axis;
        foreach (var bad in new[] { station with { Id = Guid.Empty }, station with { Name = new string('a', 65) }, station with { Name = "1station" }, station with { DeviceId = Guid.NewGuid() }, station with { EnabledEntries = [entries[0], entries[0]] }, station with { EnabledEntries = [entries[0] with { Name = "Changed" }] }, station with { EnabledEntries = [entries[0] with { BitLength = 32 }] }, station with { EnabledEntries = [entries[0] with { Type = "INT" }] }, station with { EnabledEntries = null! }, station with { EnabledEntries = [null!] }, station with { EnabledEntries = [entries[0] with { Type = null! }] }, station with { EnabledEntries = [entries[0] with { Name = null! }] } })
        { project.Hardware.Stations[0] = bad; Invalid("invalid station"); }
        project.Hardware.Stations = [station, station]; Invalid("duplicate station ID/name");
        project.Hardware.Stations = [station with { AliasAddress = 1 }, station with { Id = Guid.NewGuid(), Name = "Other", AliasAddress = 1 }]; Invalid("duplicate nonzero alias");
        project.Hardware.Stations = [null!]; Invalid("null station");
        project.Hardware.Stations = Enumerable.Range(0, 257).Select(i => station with { Id = Guid.NewGuid(), Name = "Station" + i }).ToList(); Invalid("station bound");
        project.Hardware.Stations = [station]; Valid();
        var configuredNetwork = project.Network; project.Network = null;
        Check(ProjectValidator.Capture(project, docs).HardwareConfigurationJson is not null, "station-only project captures topology");
        project.Network = configuredNetwork;
        var snapshot = ProjectValidator.Capture(project, docs);
        Check(snapshot.SourceHash == legacy.SourceHash, "configuration does not change ST identity");
        Check(ProjectValidator.Hash(snapshot.HardwareConfigurationJson!) == snapshot.HardwareHash, "frozen JSON covered by hardware identity");
        var file = Path.GetFullPath("artifacts/configuration-tests/Network.stproj");
        await new ProjectStore().SaveAsync(file, project, docs);
        var loaded = await new ProjectStore().LoadAsync(file);
        Check(ProjectValidator.Capture(loaded.Project, loaded.Sources).HardwareHash == snapshot.HardwareHash, "save/reopen preserves all metadata identity");
        Check(loaded.Project.Hardware.Stations![0].EnabledEntries.SequenceEqual(entries) && loaded.Project.Hardware.Io[0].StationId == station.Id && loaded.Project.Hardware.Axes[0].StationId == station.Id && loaded.Project.Network == network, "save/reopen preserves station, PDO, IO, axis and network");
        var originalFile = await File.ReadAllTextAsync(file);
        project.Network = network with { SshUser = "plc;id" };
        try { await new ProjectStore().SaveAsync(file, project, docs); throw new Exception("invalid save accepted"); } catch (InvalidDataException) { }
        Check(await File.ReadAllTextAsync(file) == originalFile, "invalid save protects original");
        project.Network = network with { EtherCatInterface = "eth1" }; project.Hardware.Stations.Clear(); entries[0] = entries[0] with { Name = "Mutated" };
        using var frozen = JsonDocument.Parse(snapshot.HardwareConfigurationJson!);
        Check(frozen.RootElement.GetProperty("Network").GetProperty("EtherCatInterface").GetString() == "eth0" && frozen.RootElement.GetProperty("Hardware").GetProperty("Stations").GetArrayLength() == 1 && frozen.RootElement.GetProperty("Hardware").GetProperty("Stations")[0].GetProperty("EnabledEntries")[0].GetProperty("Name").GetString() == "Input", "snapshot freezes network/station/entry collections");
        foreach (var field in new[] { "ManagementHost", "SshUser", "RuntimeEndpoint", "EtherCatInterface" })
        { var node = JsonNode.Parse(originalFile)!; node["Project"]!["Network"]![field] = null; var nullProject = JsonSerializer.Deserialize<StProject>(node["Project"]!.ToJsonString())!; Check(ProjectValidator.Validate(nullProject, docs).Count > 0, "null network rejected"); }
        Console.WriteLine("PASS hardware/network persistence, legacy identities, validation bounds/references, null safety and immutable snapshot");
        void Valid() => Check(ProjectValidator.Validate(project, docs).Count == 0, "valid configuration");
        void Invalid(string name) => Check(ProjectValidator.Validate(project, docs).Count > 0, name + " rejected");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); }
}


