using System.Globalization;
using System.Xml;
using System.Xml.Linq;
namespace StWorkbench.Core;
public sealed record PdoEntry(ushort Index, byte SubIndex, int BitLength, string Name, string Type, bool IsInput);
public sealed record EtherCatDevice(Guid Id, string Name, uint VendorId, uint ProductCode, uint Revision, IReadOnlyList<PdoEntry> Entries);
public static class EsiImporter
{
    public static IReadOnlyList<EtherCatDevice> Read(Stream stream)
    {
        try
        {
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8 * 1024 * 1024, CloseInput = false });
            var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("ESI 文件为空。");
            if (root.Name.LocalName != "EtherCATInfo") throw new InvalidDataException("需要 EtherCATInfo ESI 文件。");
            XElement? Child(XElement element, string name) => element.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            string Value(XElement element, string name) => Child(element, name)?.Value.Trim() ?? "";
            uint Number(string value)
            {
                var hex = value.StartsWith("#x", StringComparison.OrdinalIgnoreCase) || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
                return uint.Parse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture);
            }
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
                var entries = new List<PdoEntry>();
                foreach (var pdo in device.Elements().Where(e => e.Name.LocalName is "TxPdo" or "RxPdo"))
                foreach (var entry in pdo.Elements().Where(e => e.Name.LocalName == "Entry"))
                {
                    if (entries.Count >= 65536) throw new InvalidDataException("PDO 项数量超出限制。");
                    var length = checked((int)Number(Value(entry, "BitLen")));
                    if (length is < 1 or > 65536) throw new InvalidDataException("PDO 位宽无效。");
                    entries.Add(new(checked((ushort)Number(Value(entry, "Index"))), checked((byte)Number(Value(entry, "SubIndex"))), length, Value(entry, "Name"), Value(entry, "DataType"), pdo.Name.LocalName == "TxPdo"));
                }
                devices.Add(new(Guid.NewGuid(), name, vendorId, Number(type.Attribute("ProductCode")?.Value ?? ""), Number(type.Attribute("RevisionNo")?.Value ?? "0"), entries.AsReadOnly()));
            }
            if (devices.Count == 0) throw new InvalidDataException("ESI 中没有设备。");
            return devices.AsReadOnly();
        }
        catch (Exception ex) when (ex is XmlException or FormatException or OverflowException) { throw new InvalidDataException("ESI 格式或数值无效。", ex); }
    }
}
