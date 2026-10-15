using System.Security.Cryptography;
using System.Text;
using StWorkbench.Core;
using StWorkbench.Toolchain;

internal static class RealCompilerSmoke
{
    public static async Task RunAsync(string executable)
    {
        var buildRoot = Path.GetFullPath(Path.Combine("artifacts", "real-compiler-smoke"));
        static BuildSnapshot Snapshot(string text) => new(Guid.NewGuid(), "Real compiler smoke",
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), "unconfigured-hardware",
            [new(Guid.NewGuid(), "sources/Main with spaces.st", text)], []);
        var compiler = new ExternalCompiler(expectedCompilerVersion: Environment.GetEnvironmentVariable("ST_COMPILER_VERSION"));
        var success = await compiler.CompileAsync(Snapshot("PROGRAM Main\nVAR\n    Counter : DINT := 0;\nEND_VAR\nCounter := Counter + 1;\nEND_PROGRAM\n"), executable, buildRoot);
        if (!success.Success) throw new Exception("Real compiler rejected valid ST: " + success.Log);
        Console.WriteLine("REAL compiler version: " + success.CompilerVersion);
        Console.WriteLine("REAL generated artifacts: " + success.OutputDirectory);
        var failure = await compiler.CompileAsync(Snapshot("PROGRAM Main\nVAR\n    Counter : DINT := 0;\nEND_VAR\nCounter := MissingValue + 1;\nEND_PROGRAM\n"), executable, buildRoot);
        if (failure.Success) throw new Exception("Real compiler accepted undeclared variable");
        if (!failure.Diagnostics.Any(d => d.File == "sources/Main with spaces.st" && d.Line == 5 && d.Column > 0 && d.Severity == "error"))
            throw new Exception("Real compiler source diagnostic not mapped: " + failure.Log);
        Console.WriteLine("REAL located diagnostic: " + string.Join("; ", failure.Diagnostics.Select(d => $"{d.File}:{d.Line}:{d.Column}: {d.Severity}: {d.Message}")));
        var configuration = await compiler.CompileAsync(Snapshot("PROGRAM Main\nVAR\n    Counter : DINT := 0;\nEND_VAR\nCounter := Counter + 1;\nEND_PROGRAM\n\nCONFIGURATION SmokeConfig\nRESOURCE MainResource ON PLC\nTASK Cyclic(INTERVAL := T#10ms, PRIORITY := 0);\nPROGRAM MainInstance WITH Cyclic : Main;\nEND_RESOURCE\nEND_CONFIGURATION\n"), executable, buildRoot);
        if (!configuration.Success) throw new Exception("Real compiler rejected explicit ST task/configuration: " + configuration.Log);
        var generatedHeader = await File.ReadAllTextAsync(Path.Combine(configuration.OutputDirectory, "program.hpp"));
        if (!generatedHeader.Contains("SMOKECONFIG", StringComparison.OrdinalIgnoreCase)) throw new Exception("Configuration missing from generated header");
        Console.WriteLine("REAL explicit configuration artifacts: " + configuration.OutputDirectory);
    }
}
