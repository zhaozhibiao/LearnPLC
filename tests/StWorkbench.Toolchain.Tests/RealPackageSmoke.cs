using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class RealPackageSmoke
{
    public static async Task RunAsync(string adapter, string distribution)
    {
        var programId = Guid.Parse("fa0f5329-48df-49f9-a0c8-a0d61113b7fe");
        var project = new StProject { Id = Guid.Parse("8149bf92-0b3f-46a7-89cb-73acba7c3962"), Name = "Counter demo", Items = [new ProjectItem { Id = programId, Name = "Main" }], Tasks = [new PlcTask { ProgramId = programId }] };
        var docs = new SourceDocument[] { new(programId, "sources/Main.st", "PROGRAM Main\nVAR Counter : DINT := 0; END_VAR\nCounter := Counter + 1;\nEND_PROGRAM") };
        var snapshot = ProjectValidator.Capture(project, docs);
        if (Environment.GetEnvironmentVariable("ST_EXPORT_DEMO") == "1") await new ProjectStore().SaveAsync(Path.GetFullPath("examples/counter/Counter.stproj"), project, docs);
        var names = new Dictionary<Guid, string> { [programId] = "Main" };
        var builder = new PackageBuilder();
        var root = Path.GetFullPath(Path.Combine("artifacts", "real-package-smoke"));
        var result = await builder.BuildAsync(snapshot, names, "node", adapter, distribution, root);
        if (!result.Success) throw new Exception("Real package build failed: " + result.Compilation.Log);
        if (result.SafeForDirectUpload) throw new Exception("Unsafe native upload advertised");
        using var zip = ZipFile.OpenRead(result.PackagePath);
        foreach (var file in new[] { "generated.hpp", "generated_debug.cpp", "debug-map.json", "defines.h", "build-manifest.json", "strucpp_runtime/include/debug_table.hpp", "COPYING.RUNTIME" })
            if (zip.GetEntry(file) is null) throw new Exception("Package file missing: " + file);
        var header = await File.ReadAllTextAsync(Path.Combine(result.Compilation.OutputDirectory, "generated.hpp"));
        if (!header.Contains("Configuration_CONFIG0")) throw new Exception("Pinned Runtime configuration name missing");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.Compilation.OutputDirectory, "build-manifest.json")));
        if (manifest.RootElement.GetProperty("RuntimeCommit").GetString() != "a9e38d901fb87deb5a8cf778b52639b9387834d0") throw new Exception("Runtime commit not pinned");
        if (string.IsNullOrEmpty(result.SymbolTableHash) || string.IsNullOrEmpty(result.BuildHash)) throw new Exception("Package identity missing");
        if (manifest.RootElement.GetProperty("CommercialRedistributionCleared").GetBoolean() || manifest.RootElement.GetProperty("RuntimeLinkVerified").GetBoolean()) throw new Exception("Unverified compatibility/license status accepted");
        if (!manifest.RootElement.TryGetProperty("RuntimeHeaderRedistributionCleared", out var headerCleared) || !headerCleared.GetBoolean() || manifest.RootElement.GetProperty("UnmarkedRuntimeHeaders").GetArrayLength() != 0)
            throw new Exception("Independent MIT headers not integrated/audited");
        if (zip.GetEntry("RUNTIME-COMPAT-LICENSE") is null) throw new Exception("MIT compatibility header license missing");
        foreach (var item in manifest.RootElement.GetProperty("Files").EnumerateObject())
        {
            var entry = zip.GetEntry(item.Name) ?? throw new Exception("Manifest file absent from ZIP: " + item.Name);
            await using var stream = entry.Open();
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream)) != item.Value.GetString()) throw new Exception("ZIP hash mismatch: " + item.Name);
        }
        using var debug = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result.Compilation.OutputDirectory, "debug-map.json")));
        if (debug.RootElement.GetProperty("md5").GetString() != manifest.RootElement.GetProperty("ProgramMd5").GetString()) throw new Exception("Debug/program identity mismatch");
        if (!debug.RootElement.GetProperty("leaves").EnumerateArray().Any(leaf => leaf.GetProperty("path").GetString()!.EndsWith("COUNTER"))) throw new Exception("Authoritative variable map missing counter");
        await Task.Delay(2100); // ZIP's default timestamp resolution is two seconds.
        var repeat = await builder.BuildAsync(snapshot, names, "node", adapter, distribution, root);
        if (!repeat.Success || repeat.BuildHash != result.BuildHash) throw new Exception("Identical source/task snapshot changed build identity");
        var firstArchive = await File.ReadAllBytesAsync(result.PackagePath);
        var repeatedArchive = await File.ReadAllBytesAsync(repeat.PackagePath);
        if (!SHA256.HashData(firstArchive).SequenceEqual(SHA256.HashData(repeatedArchive)))
            throw new Exception("Identical build identity produced different archive bytes");
        project.Tasks[0].IntervalMilliseconds = 20;
        snapshot = ProjectValidator.Capture(project, docs);
        var second = await builder.BuildAsync(snapshot, names, "node", adapter, distribution, root);
        if (!second.Success || second.BuildHash == result.BuildHash) throw new Exception("Task timing change did not change identity");
        var invalidNames = new Dictionary<Guid, string> { [programId] = "OtherProgram" };
        var invalid = await builder.BuildAsync(snapshot, invalidNames, "node", adapter, distribution, root);
        if (invalid.Success || !string.IsNullOrEmpty(invalid.PackagePath)) throw new Exception("Mismatched program mapping packaged");
        var broken = snapshot with { Sources = [new(programId, "sources/Main.st", "PROGRAM Main\nMissingVariable := 1;\nEND_PROGRAM")] };
        var failedCompile = await builder.BuildAsync(broken, names, "node", adapter, distribution, root);
        if (failedCompile.Success || !string.IsNullOrEmpty(failedCompile.PackagePath) || !failedCompile.Compilation.Diagnostics.Any(d => d.File == "sources/Main.st" && d.Line == 2)) throw new Exception("Invalid ST packaged or source diagnostics lost");
        Console.WriteLine("REAL staging candidate ZIP: " + result.PackagePath);
        Console.WriteLine("REAL task-change candidate ZIP: " + second.PackagePath);
    }
}
