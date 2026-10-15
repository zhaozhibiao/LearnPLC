using System.Globalization;
using System.Text.RegularExpressions;

namespace StWorkbench.Core;

public sealed record IoAutoMappingPlan(IReadOnlyList<IoMapping> Added, IReadOnlyList<string> Skipped);

public static class IoAutoMapping
{
    private const int AddressBits = 65536 * 8;
    private static readonly Regex AddressPattern = new(@"^%([IQ])([XBWDL])([0-9]+)(?:\.([0-9]+))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IoAutoMappingPlan Plan(HardwareConfiguration hardware, EtherCatStation station)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        ArgumentNullException.ThrowIfNull(station);
        var input = new Ranges();
        var output = new Ranges();
        var names = new HashSet<string>(hardware.Io.Select(io => io.Name).Concat(hardware.Axes.Select(axis => axis.Name)), StringComparer.OrdinalIgnoreCase);
        var mapped = hardware.Io.Where(io => io.StationId == station.Id && io.PdoIndex.HasValue && io.PdoSubIndex.HasValue)
            .Select(io => (io.IsInput, Index: io.PdoIndex!.Value, SubIndex: io.PdoSubIndex!.Value)).ToHashSet();
        foreach (var io in hardware.Io)
        {
            var (start, width) = ExistingRange(io);
            (io.IsInput ? input : output).Reserve(start, width);
        }
        var added = new List<IoMapping>();
        var skipped = new List<string>();
        foreach (var entry in station.EnabledEntries)
        {
            var label = $"PDO 0x{entry.Index:X4}:{entry.SubIndex} ({entry.Type}, {entry.BitLength} 位)";
            if (!mapped.Add((entry.IsInput, entry.Index, entry.SubIndex))) continue;
            var type = Describe(entry.Type);
            if (type.Width == 0)
            {
                skipped.Add(label + "：不支持自动映射此类型");
                continue;
            }
            if (entry.BitLength != type.Width)
            {
                skipped.Add(label + $"：类型要求 {type.Width} 位，PDO 位宽不匹配");
                continue;
            }
            var start = (entry.IsInput ? input : output).Allocate(type.Width);
            var address = $"%{(entry.IsInput ? 'I' : 'Q')}{type.Code}" + (type.Width == 1
                ? $"{start / 8}.{start % 8}"
                : (start / type.Width).ToString(CultureInfo.InvariantCulture));
            var name = UniqueName(station.Name, entry, names);
            added.Add(new(name, address, type.Name, entry.IsInput)
            {
                StationId = station.Id, PdoIndex = entry.Index, PdoSubIndex = entry.SubIndex
            });
        }
        return new(added.AsReadOnly(), skipped.AsReadOnly());
    }

    private static (int Start, int Width) ExistingRange(IoMapping io)
    {
        InvalidDataException Invalid() => new($"已有 IO“{io.Name}”地址“{io.Address}”无法安全自动分配：地址、方向、位号或占用范围无效");
        var match = AddressPattern.Match(io.Address ?? "");
        if (!match.Success || char.ToUpperInvariant(match.Groups[1].Value[0]) != (io.IsInput ? 'I' : 'Q') ||
            !long.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) throw Invalid();
        var code = char.ToUpperInvariant(match.Groups[2].Value[0]);
        var addressWidth = code switch { 'X' => 1, 'B' => 8, 'W' => 16, 'D' => 32, 'L' => 64, _ => 0 };
        var bit = 0;
        if (code == 'X')
        {
            if (!match.Groups[4].Success || !int.TryParse(match.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out bit) || bit is < 0 or > 7 || index > 65535) throw Invalid();
        }
        else if (match.Groups[4].Success || index >= AddressBits / addressWidth) throw Invalid();
        var type = Describe(io.Type);
        if (type.Width == 0) throw new InvalidDataException($"已有 IO“{io.Name}”类型“{io.Type}”未知，无法安全自动分配");
        var width = Math.Max(addressWidth, type.Width);
        var start = code == 'X' ? index * 8 + bit : index * addressWidth;
        if (start < 0 || start + width > AddressBits) throw Invalid();
        return ((int)start, width);
    }

    private static (string Name, int Width, char Code) Describe(string? type) => type?.ToUpperInvariant() switch
    {
        "BOOL" or "BOOLEAN" => ("BOOL", 1, 'X'),
        "BYTE" or "USINT" or "UINT8" => ("BYTE", 8, 'B'),
        "WORD" or "UINT" or "UINT16" => ("WORD", 16, 'W'),
        "INT" or "INT16" => ("INT", 16, 'W'),
        "DWORD" or "UDINT" or "UINT32" => ("DWORD", 32, 'D'),
        "DINT" or "INT32" => ("DINT", 32, 'D'),
        "REAL" or "REAL32" => ("REAL", 32, 'D'),
        "LREAL" or "REAL64" => ("LREAL", 64, 'L'),
        _ => ("", 0, '\0')
    };

    private static string UniqueName(string stationName, PdoEntry entry, HashSet<string> names)
    {
        var prefix = new string(stationName.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        if (prefix.Length == 0) prefix = "Station";
        if (char.IsAsciiDigit(prefix[0])) prefix = "_" + prefix;
        var suffix = $"_{(entry.IsInput ? "DI" : "DO")}_{entry.Index:X4}_{entry.SubIndex}";
        for (var counter = 1; ; counter++)
        {
            var ending = suffix + (counter == 1 ? "" : "_" + counter.ToString(CultureInfo.InvariantCulture));
            var name = prefix[..Math.Min(prefix.Length, 63 - ending.Length)] + ending;
            if (names.Add(name)) return name;
        }
    }

    // Sorted, merged half-open intervals keep both memory and scans proportional to occupied ranges.
    private sealed class Ranges
    {
        private readonly List<(int Start, int End)> intervals = [];

        public void Reserve(int start, int width)
        {
            var end = start + width;
            var index = intervals.BinarySearch((start, end), Comparer<(int Start, int End)>.Create((a, b) => a.Start.CompareTo(b.Start)));
            if (index < 0) index = ~index;
            if (index > 0 && intervals[index - 1].End >= start) index--;
            while (index < intervals.Count && intervals[index].Start <= end)
            {
                start = Math.Min(start, intervals[index].Start);
                end = Math.Max(end, intervals[index].End);
                intervals.RemoveAt(index);
            }
            intervals.Insert(index, (start, end));
        }

        public int Allocate(int width)
        {
            var candidate = 0;
            foreach (var interval in intervals)
            {
                if (candidate + width <= interval.Start) break;
                if (candidate < interval.End) candidate = (interval.End + width - 1) / width * width;
            }
            if (candidate > AddressBits - width) throw new InvalidDataException("IO 地址空间已耗尽，无法安全自动分配；未应用任何新增映射");
            Reserve(candidate, width);
            return candidate;
        }
    }
}
