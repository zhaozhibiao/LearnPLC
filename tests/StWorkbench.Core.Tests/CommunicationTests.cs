using StWorkbench.Core;
using System.Text.Json;

internal static class CommunicationTests
{
    public static async Task RunAsync()
    {
        var item = new ProjectItem(); var p = new StProject { Items = [item], Tasks = [new() { ProgramId = item.Id }] };
        SourceDocument[] docs = [new(item.Id, item.SourcePath, "PROGRAM Main\nEND_PROGRAM")];
        var legacy = ProjectValidator.Capture(p, docs);
        p.Communication = new() { Channels = [new() { Name = "Drive", Host = "127.0.0.1" }] };
        var captured = ProjectValidator.Capture(p, docs);
        if (legacy.HardwareHash == captured.HardwareHash || captured.Communication?.Channels[0].Name != "Drive") throw new Exception("Communication configuration must enter frozen identity");
        p.Communication.Channels[0] = new() { Name = "Changed" };
        if (captured.Communication!.Channels[0].Name != "Drive") throw new Exception("Communication snapshot not frozen");
        p.Communication = new() { Channels = [new() { Protocol = "Server" }] };
        if (ProjectValidator.Validate(p, docs).Count == 0) throw new Exception("No server role this phase");
        foreach (var invalid in new CommunicationChannel[] {
            new() { Host = "127.1" }, new() { Host = "2130706433" }, new() { Host = "127.00.0.1" }, new() { Host = "fe80::1%lo" },
            new() { Protocol = "Serial", Physical = "RS232", Direction = "KernelRts" },
            new() { Protocol = "ModbusRtu", Framing = "Delimiter" } })
        {
            p.Communication = new() { Channels = [invalid] };
            if (ProjectValidator.Validate(p, docs).Count == 0) throw new Exception("Runtime-invalid communication configuration accepted: " + JsonSerializer.Serialize(invalid));
        }
        foreach (var host in new[] { "127.0.0.1", "0.0.0.0", "::1", "::ffff:192.168.1.1", "2001:db8::1" })
        {
            p.Communication = new() { Channels = [new() { Host = host }] };
            if (ProjectValidator.Validate(p, docs).Count != 0) throw new Exception("Supported literal IP rejected: " + host);
        }
        p.Communication = null;
        if (ProjectValidator.Capture(p, docs).HardwareHash != legacy.HardwareHash) throw new Exception("Legacy identity changed");
        if (JsonSerializer.Serialize(p).Contains("Communication")) throw new Exception("Legacy null field must be omitted");
        p.Communication = new() { Channels = [new() { Protocol = "ModbusRtu", Device = "/dev/ttyUSB0", Physical = "RS485" }] };
        var root = Path.Combine(Path.GetTempPath(), "st-comm-" + Guid.NewGuid().ToString("N"));
        try { var path = Path.Combine(root, "P.stproj"); await new ProjectStore().SaveAsync(path, p, docs); if ((await new ProjectStore().LoadAsync(path)).Project.Communication is null) throw new Exception("Communication lost on save"); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        Console.WriteLine("PASS communication configuration validation, legacy omission, immutable capture and persistence");
        var symbols = BuiltinLibraryCatalog.PublicSymbols(Directory.GetCurrentDirectory(), [new(Guid.Empty,"Main.st","PROGRAM Main\nVAR\ncontroller : WB_PID;\nEND_VAR\nEND_PROGRAM")], p.Communication);
        var language = new StLanguageService();
        if (language.Signature(symbols, "controller")?.Contains("Setpoint", StringComparison.OrdinalIgnoreCase) != true || !language.Complete(symbols,"WB_MB").Any(s => s.Name=="WB_MB_REQUEST") || symbols.Any(s => s.Name.StartsWith("CB_"))) throw new Exception("Public library instance signatures/completion not available");
        SourceDocument[] statefulSources = [new(Guid.Empty,"invalid.st","FUNCTION_BLOCK Wrapper\nVAR\nRequest : ARRAY[0..1] OF WB_MB_REQUEST;\nEND_VAR\nEND_FUNCTION_BLOCK\nFUNCTION Bad : BOOL\nVAR\nLocal : Wrapper;\nEND_VAR\nEND_FUNCTION")];
        if (CommunicationBinding.Validate(p.Communication,statefulSources,p.Tasks).Count==0) throw new Exception("Nested stateful communication in FUNCTION must be rejected");
        if (!ControlLibraryBinding.IsUsed([new(Guid.Empty,"helper.st","value := wb_finite(1.0);")])) throw new Exception("Control helper detection must be case insensitive");
    }
}
