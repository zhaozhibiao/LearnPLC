using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
namespace StWorkbench.Core;
public sealed record PdoEntry(ushort Index, byte SubIndex, int BitLength, string Name, string Type, bool IsInput);
public sealed record EtherCatDevice(Guid Id, string Name, uint VendorId, uint ProductCode, uint Revision, IReadOnlyList<PdoEntry> Entries)
{
    public EtherCatDeviceDescription? Description { get; init; }
}
public static class EsiImporter
{
    private static XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    private static IEnumerable<XElement> Children(XElement element, string name) => element.Elements().Where(e => e.Name.LocalName == name);
    private static string Value(XElement element, string name) => Child(element, name)?.Value.Trim() ?? "";
    private static uint Number(string value)
    {
        var hex = value.StartsWith("#x", StringComparison.OrdinalIgnoreCase) || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return uint.Parse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture);
    }
    private static bool Boolean(string value) => value switch
    {
        "1" or "true" => true, "0" or "false" => false,
        _ => throw new InvalidDataException("ESI 布尔值无效。")
    };
    private static bool? BoolAttribute(XElement element, string name) => element.Attribute(name) is { } a ? Boolean(a.Value) : null;
    private static uint? NumAttribute(XElement element, string name) => element.Attribute(name) is { } a ? Number(a.Value) : null;
    private static long Signed(string value) => long.Parse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
    public static IReadOnlyList<EtherCatDevice> Read(Stream stream)
    {
        try
        {
            using var source = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (source.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("ESI 文件超出大小限制。");
                source.Write(buffer, 0, read);
            }
            var hash = Convert.ToHexString(SHA256.HashData(source.ToArray())).ToLowerInvariant();
            source.Position = 0;
            using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024, CloseInput = false });
            var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("ESI 文件为空。");
            if (root.Name.LocalName != "EtherCATInfo") throw new InvalidDataException("需要 EtherCATInfo ESI 文件。");
            var vendor = Child(root, "Vendor") ?? throw new InvalidDataException("缺少厂商信息。");
            var vendorId = Number(Value(vendor, "Id"));
            var devices = new List<EtherCatDevice>();
            foreach (var device in root.Descendants().Where(e => e.Name.LocalName == "Device"))
            {
                if (devices.Count >= 1024) throw new InvalidDataException("ESI 设备数量超出限制。");
                var type = Child(device, "Type") ?? throw new InvalidDataException("设备缺少 Type。");
                var name = Value(device, "Name");
                if (string.IsNullOrWhiteSpace(name)) name = type.Value.Trim();
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("设备缺少名称。");
                var unsupported = new List<string>();
                void Unknown(XElement parent, params string[] known)
                {
                    foreach (var element in parent.Elements().Where(e => !known.Contains(e.Name.LocalName)))
                        unsupported.Add(parent.Name.LocalName + "/" + element.Name.LocalName);
                }
                Unknown(device, "Type", "Name", "GroupType", "URL", "Info", "Profile", "Fmmu", "Sm", "RxPdo", "TxPdo", "Mailbox", "Dc", "Eeprom", "Physics", "HideType", "Image16x14", "ImageFile16x14", "ImageData16x14");
                // Alternative configuration and non-CoE mailboxes require an explicit runtime implementation.
                var entries = new List<PdoEntry>();
                var groups = new List<EtherCatPdoGroup>();
                foreach (var pdo in device.Elements().Where(e => e.Name.LocalName is "TxPdo" or "RxPdo"))
                {
                    if (groups.Count >= 512) throw new InvalidDataException("PDO 组数量超出限制。");
                    Unknown(pdo, "Index", "Name", "Exclude", "Entry");
                    foreach (var attribute in pdo.Attributes().Where(a => a.Name.LocalName is not ("Sm" or "Fixed" or "Mandatory" or "Virtual" or "OverwrittenByModule")))
                        unsupported.Add("PDO/@" + attribute.Name.LocalName);
                    if (BoolAttribute(pdo, "Virtual") == true || BoolAttribute(pdo, "OverwrittenByModule") == true) unsupported.Add("PDO virtual/module mapping");
                    var isInput = pdo.Name.LocalName == "TxPdo";
                    var groupEntries = new List<PdoEntry>();
                    foreach (var entry in Children(pdo, "Entry"))
                    {
                        Unknown(entry, "Index", "SubIndex", "BitLen", "Name", "DataType", "Comment");
                        foreach (var a in entry.Attributes()) unsupported.Add("PDO Entry/@" + a.Name.LocalName);
                        if (entries.Count >= 65536) throw new InvalidDataException("PDO 项数量超出限制。");
                        var length = checked((int)Number(Value(entry, "BitLen")));
                        if (length is < 1 or > 65536) throw new InvalidDataException("PDO 位宽无效。");
                        var index = checked((ushort)Number(Value(entry, "Index")));
                        // Index zero is ESI padding; its optional SubIndex defaults to zero.
                        var subIndex = Value(entry, "SubIndex");
                        var parsed = new PdoEntry(index, checked((byte)Number(subIndex.Length == 0 && index == 0 ? "0" : subIndex)), length, Value(entry, "Name"), Value(entry, "DataType"), isInput);
                        entries.Add(parsed); groupEntries.Add(parsed);
                    }
                    var sm = NumAttribute(pdo, "Sm");
                    if (sm > 15) throw new InvalidDataException("PDO SyncManager 无效。");
                    var mandatory = BoolAttribute(pdo, "Mandatory") ?? false;
                    groups.Add(new(checked((ushort)Number(Value(pdo, "Index"))), Value(pdo, "Name"), isInput, sm is null ? null : (int)sm, BoolAttribute(pdo, "Fixed") ?? false, mandatory, Children(pdo, "Exclude").Select(e => checked((ushort)Number(e.Value.Trim()))).ToArray(), sm is not null || mandatory, groupEntries.AsReadOnly()));
                }
                if (groups.GroupBy(g => g.Index).Any(g => g.Count() > 1)) unsupported.Add("Duplicate PDO index");
                foreach (var selected in groups.Where(g => g.DefaultSelected))
                    if (groups.Any(other => other.DefaultSelected && other.Index != selected.Index && (selected.Excludes.Contains(other.Index) || other.Excludes.Contains(selected.Index)))) unsupported.Add("Conflicting default PDO selection");
                var managers = new List<EtherCatSyncManager>();
                foreach (var sm in Children(device, "Sm"))
                {
                    if (managers.Count >= 16) throw new InvalidDataException("SyncManager 数量超出限制。");
                    foreach (var attribute in sm.Attributes().Where(a => a.Name.LocalName is not ("StartAddress" or "DefaultSize" or "ControlByte" or "Enable" or "Watchdog" or "MinSize" or "MaxSize"))) unsupported.Add("Sm/@" + attribute.Name.LocalName);
                    var address = NumAttribute(sm, "StartAddress");
                    var size = NumAttribute(sm, "DefaultSize");
                    if (address > ushort.MaxValue || size > ushort.MaxValue) throw new InvalidDataException("SyncManager 范围无效。");
                    managers.Add(new(managers.Count, sm.Value.Trim(), address, size is null ? null : (int)size, NumAttribute(sm, "ControlByte") is { } control ? checked((byte)control) : null, BoolAttribute(sm, "Enable"), BoolAttribute(sm, "Watchdog")));
                }
                var modes = new List<EtherCatDcMode>();
                if (Child(device, "Dc") is { } dc)
                {
                    Unknown(dc, "OpMode");
                    foreach (var mode in Children(dc, "OpMode"))
                    {
                        if (modes.Count >= 64) throw new InvalidDataException("DC 模式数量超出限制。");
                        Unknown(mode, "Name", "Desc", "AssignActivate", "CycleTimeSync0", "CycleTimeSync1", "ShiftTimeSync0", "ShiftTimeSync1");
                        EtherCatDcTiming? Timing(string suffix)
                        {
                            var cycle = Child(mode, "CycleTimeSync" + suffix);
                            var shift = Child(mode, "ShiftTimeSync" + suffix);
                            if (cycle is null && shift is null) return null;
                            foreach (var a in (cycle?.Attributes() ?? []).Where(a => a.Name.LocalName != "Factor")) unsupported.Add("DC cycle/@" + a.Name.LocalName);
                            if (shift?.HasAttributes == true) unsupported.Add("DC shift attributes");
                            var value = cycle is null ? 0 : Signed(cycle.Value.Trim());
                            if (value < 0 || value > uint.MaxValue) throw new InvalidDataException("DC 周期无效。");
                            var factor = cycle?.Attribute("Factor") is { } f ? checked((int)Signed(f.Value)) : (int?)null;
                            if (factor is < -65535 or > 65535) throw new InvalidDataException("DC 周期因子无效。");
                            var shiftValue = shift is null ? (long?)null : Signed(shift.Value.Trim());
                            if (shiftValue is < int.MinValue or > int.MaxValue) throw new InvalidDataException("DC 偏移无效。");
                            return new(value, factor, shiftValue);
                        }
                        modes.Add(new(Value(mode, "Name"), checked((ushort)Number(Value(mode, "AssignActivate"))), Timing("0"), Timing("1")));
                    }
                }
                var commands = new List<EtherCatStartupCommand>();
                if (Child(device, "Mailbox") is { } mailbox)
                {
                    Unknown(mailbox, "CoE");
                    if (Child(mailbox, "CoE") is { } coe)
                    {
                        Unknown(coe, "InitCmds");
                        if (Child(coe, "InitCmds") is { } init)
                        {
                            Unknown(init, "InitCmd");
                            foreach (var command in Children(init, "InitCmd"))
                            {
                                if (commands.Count >= 1024) throw new InvalidDataException("启动命令数量超出限制。");
                                Unknown(command, "Transition", "Index", "SubIndex", "Data", "Comment");
                                foreach (var a in command.Attributes().Where(a => a.Name.LocalName != "CompleteAccess")) unsupported.Add("InitCmd/@" + a.Name.LocalName);
                                foreach (var field in new[] { "Index", "SubIndex", "Data" })
                                    if (Children(command, field).Count() != 1) unsupported.Add("InitCmd ambiguous " + field);
                                var data = Value(command, "Data");
                                if (data.Length is < 2 or > 8192 || data.Length % 2 != 0 || data.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("启动命令 Data 必须为受限长度的十六进制字节。");
                                var transition = Value(command, "Transition");
                                if (transition.Length is < 1 or > 64) throw new InvalidDataException("启动命令 Transition 无效。");
                                if (transition is not ("IP" or "PI" or "PS" or "SP" or "SO" or "OS" or "OP" or "SI" or "OI")) unsupported.Add("InitCmd transition: " + transition);
                                if (Children(command, "Transition").Count() != 1) unsupported.Add("InitCmd multiple transitions");
                                commands.Add(new(transition, checked((ushort)Number(Value(command, "Index"))), checked((byte)Number(Value(command, "SubIndex"))), data.ToLowerInvariant(), BoolAttribute(command, "CompleteAccess") ?? false));
                            }
                        }
                    }
                }
                devices.Add(new(Guid.NewGuid(), name, vendorId, Number(type.Attribute("ProductCode")?.Value ?? ""), Number(type.Attribute("RevisionNo")?.Value ?? "0"), entries.AsReadOnly())
                {
                    Description = new(hash, groups.AsReadOnly(), managers.AsReadOnly(), modes.AsReadOnly(), commands.AsReadOnly(), unsupported.Distinct().ToArray())
                });
            }
            if (devices.Count == 0) throw new InvalidDataException("ESI 中没有设备。");
            return devices.AsReadOnly();
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException) { throw new InvalidDataException("ESI 格式或数值无效。", ex); }
    }
}
