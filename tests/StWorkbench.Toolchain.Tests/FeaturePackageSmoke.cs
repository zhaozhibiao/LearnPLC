using StWorkbench.Core;
using StWorkbench.Toolchain;
using System.IO.Compression;
using System.Text.Json;

internal static class FeaturePackageSmoke
{
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath("artifacts/control-comm-package"); Directory.CreateDirectory(root);
        var repository = Directory.GetCurrentDirectory();
        var main = new ProjectItem { Id = Guid.Parse("b12b717d-ef7c-47e9-9369-9b22d1c90610") };
        var p = new StProject { Id = Guid.Parse("e0668f38-208e-46d5-a96d-df6bfffd4bc4"), Name = "控制与通信示例", Items = [main], Tasks = [new() { ProgramId = main.Id }], Communication = new() { Channels = [
            new() { Name = "Bus", Host = "127.0.0.1", Port = 15020 },
            new() { Name = "RawTcp", Protocol = "Tcp", Host = "127.0.0.1", Port = 15021 },
            new() { Name = "Serial", Protocol = "Serial", Device = "/dev/ttyUSB0" },
            new() { Name = "Rtu", Protocol = "ModbusRtu", Device = "/dev/ttyUSB1", Physical = "RS485" }
        ] } };
        var snippets = BuiltinLibraryCatalog.ControlInstructions(repository, .01).Concat(BuiltinLibraryCatalog.CommunicationInstructions(p.Communication)).ToArray();
        if (snippets.Length != 26) throw new Exception("Missing authoritative library templates");
        var source = "PROGRAM Main\nVAR\nEND_VAR\n\nEND_PROGRAM\n";
        foreach (var instruction in snippets) source = StSnippetInsertion.Insert(source, source.IndexOf("END_PROGRAM", StringComparison.Ordinal), instruction).Text;
        var snapshot = ProjectValidator.Capture(p, [new(main.Id, main.SourcePath, source)]);
        var builder = new PackageBuilder();
        var adapter = Path.Combine(repository, "tools/compiler-adapter/compile.mjs");
        var distribution = Path.Combine(repository, ".tools/strucpp-0.7.0/strucpp");
        var result = await builder.BuildAsync(snapshot, new Dictionary<Guid, string> { [main.Id] = "Main" }, "node", adapter, distribution, root);
        if (!result.Success) throw new Exception("Control+comm compile: " + result.Compilation.Log);
        using var zip = ZipFile.OpenRead(result.PackagePath);
        foreach (var file in new[] { "control/control.st", "control/LICENSE", "communication.st", "communication_bridge.cpp", "generated_communication_scan_shim.cpp", "communication_lifecycle.inc", "native/src/communication_worker.cpp", "native/LICENSE", "communication-manifest.json" })
            if (zip.GetEntry(file) is null) throw new Exception("Missing feature dependency: " + file);
        using var manifest = JsonDocument.Parse(zip.GetEntry("build-manifest.json")!.Open());
        if (manifest.RootElement.GetProperty("CommunicationInterfaceVersion").GetString() != CommunicationBinding.Interface || manifest.RootElement.GetProperty("ControlLibraryVersion").GetString() != "1.0.0") throw new Exception("Missing feature identity");
        await File.WriteAllTextAsync(Path.Combine(root, "last-package.txt"), result.PackagePath);
        await File.WriteAllTextAsync(Path.Combine(root, "example.st"), source);
        await new ProjectStore().SaveAsync(Path.Combine(repository, "examples/control-communication/ControlCommunication.stproj"), p, [new(main.Id, main.SourcePath, source)]);
        var combined = await new ProjectStore().LoadAsync(Path.Combine(repository, "examples/continuous-path/Continuous.stproj"));
        combined.Project.Communication = p.Communication;
        var combinedSources = combined.Sources.ToArray();
        var text = combinedSources[0].Text;
        foreach (var instruction in new[] { snippets[0], snippets.Single(s => s.Id == "Comm_Bus_WB_MB_REQUEST"), new StInstruction("axis", "Axis", "test", "test", "{p}(Axis := AXIS_X, Enable := FALSE);", ["{p} : MC_Power;"], Stateful: true) })
            text = StSnippetInsertion.Insert(text, text.IndexOf("END_PROGRAM", StringComparison.Ordinal), instruction).Text;
        combinedSources[0] = combinedSources[0] with { Text = text };
        var combinedResult = await builder.BuildAsync(ProjectValidator.Capture(combined.Project, combinedSources), combined.Project.Items.Where(i => i.Kind == ItemKind.Program).ToDictionary(i => i.Id, i => i.Name), "node", adapter, distribution, root);
        if (!combinedResult.Success) throw new Exception("Combined axis/path/control/communication compilation: " + combinedResult.Compilation.Log);
        await File.WriteAllTextAsync(Path.Combine(root, "combined-package.txt"), combinedResult.PackagePath);
        Console.WriteLine("PASS all 15 real control templates + communication compile, exact library inventory and composed scan hook");
        Console.WriteLine("PASS simultaneous axis, continuous path, control and communication actual ST compilation");
    }
}
