using System.Globalization;
using System.Text.RegularExpressions;

namespace StWorkbench.Core;

public static class BuiltinLibraryCatalog
{
    public static IReadOnlyList<StSymbol> PublicSymbols(string repository, IReadOnlyList<SourceDocument> sources, CommunicationConfiguration? communication)
    {
        var documents = sources.ToList();
        foreach (var path in new[] { "tools/control-library/control.st", "tools/communication-bridge/communication.st" })
            documents.Add(new(Guid.Empty, path, File.ReadAllText(Path.Combine(repository, path))));
        if (communication is not null) documents.Add(new(Guid.Empty, "communication-constants.st", CommunicationBinding.Constants(communication)));
        return new StLanguageService().Index(documents).Where(s => !s.Name.StartsWith("CB_", StringComparison.OrdinalIgnoreCase) && s.Owner?.StartsWith("CB_", StringComparison.OrdinalIgnoreCase) != true).ToArray();
    }
    public static IEnumerable<StInstruction> ControlInstructions(string repository, double cycleSeconds)
    {
        var source = File.ReadAllText(Path.Combine(repository, "tools", "control-library", "control.st"));
        var symbols = new StLanguageService().Index([new(Guid.Empty, "control.st", source)]);
        foreach (var name in ControlLibraryBinding.Blocks)
        {
            var body = Regex.Match(source, @"\bFUNCTION_BLOCK\s+" + name + @"\b(?<body>.*?)\bEND_FUNCTION_BLOCK\b", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups["body"].Value;
            var inputs = symbols.Where(s => s.Owner == name && s.Kind == "VAR_INPUT").ToArray();
            var declarations = new List<string> { "{p} : " + name + ";" }; var arguments = new List<string>();
            foreach (var input in inputs)
            {
                if (input.Name == "Ts") { arguments.Add("Ts := " + cycleSeconds.ToString("0.0########", CultureInfo.InvariantCulture)); continue; }
                var initial = Regex.Match(body, @"\b" + input.Name + @"\s*:\s*" + Regex.Escape(input.Type) + @"\s*:=\s*([^;]+);", RegexOptions.IgnoreCase);
                var value = initial.Success ? " := " + initial.Groups[1].Value.Trim() : "";
                if (input.Name == "Enable") value = " := FALSE"; // Inserting a block does not start a controller.
                declarations.Add("{p}" + input.Name + " : " + input.Type + value + ";");
                arguments.Add(input.Name + " := {p}" + input.Name);
            }
            yield return new(name, name.Replace("WB_", ""), "控制算法", "自研 ST 控制库；时间为秒，并联 PID 增益。请设置实际量程、周期及参数，再启用。", "{p}(" + string.Join(", ", arguments) + ");", declarations.ToArray(), Stateful: true);
        }
    }
    public static IEnumerable<StInstruction> CommunicationInstructions(CommunicationConfiguration? configuration)
    {
        foreach (var channel in configuration?.Channels ?? [])
        {
            var names = channel.Protocol is "ModbusTcp" or "ModbusRtu" ? new[] { "WB_MB_REQUEST", "WB_COMM_CLOSE" } : channel.Protocol == "Tcp" ? new[] { "WB_TCP_CONNECT", "WB_TCP_SEND", "WB_TCP_RECEIVE", "WB_TCP_CLOSE" } : new[] { "WB_SERIAL_SEND", "WB_SERIAL_RECEIVE", "WB_COMM_CLOSE" };
            foreach (var name in names)
            {
                var declarations = new List<string> { "{p} : " + name + ";", "{p}Execute : BOOL := FALSE;" };
                var arguments = "Channel := COMM_" + channel.Name + ", Execute := {p}Execute";
                if (name != "WB_COMM_CLOSE")
                {
                    declarations.AddRange(["{p}Length : UINT := 0;", "{p}Data : ARRAY[0..511] OF BYTE;"]);
                    arguments += ", DataLength := {p}Length, Data := {p}Data";
                }
                if (name == "WB_MB_REQUEST")
                {
                    declarations.AddRange(["{p}Function : UINT := 3;", "{p}Unit : UINT := 1;", "{p}Address : UINT := 0;", "{p}Quantity : UINT := 1;"]);
                    arguments += ", FunctionCode := {p}Function, UnitId := {p}Unit, Address := {p}Address, Quantity := {p}Quantity";
                }
                yield return new("Comm_" + channel.Name + "_" + name, name + " · " + channel.Name, "通信", "绑定配置通道 " + channel.Name + "。Execute 上升沿提交一次；写结果 Unknown 不可自动重试。", "{p}(" + arguments + ");", declarations.ToArray(), Stateful: true);
            }
        }
    }
}
