using System.Buffers.Binary;
using System.Globalization;
namespace StWorkbench.OpenPlc;
public sealed record DebugSymbol(string Path, string Type, DebugVariableAddress Address);
public static class DebugValueDecoder
{
    public static string? Decode(string type, ReadOnlySpan<byte> bytes, bool littleEndian)
    {
        // Pinned STruCpp debug_dispatch uses fixed len8-UTF8 / len8-UTF16LE wire windows,
        // not the compiler's in-memory IECStringVar layout.
        if (type.Equals("STRING", StringComparison.OrdinalIgnoreCase) || type.Equals("WSTRING", StringComparison.OrdinalIgnoreCase))
        {
            var wide = type.Equals("WSTRING", StringComparison.OrdinalIgnoreCase);
            if (bytes.Length != (wide ? 253 : 127) || bytes[0] > 126) return null;
            try { return (wide ? (System.Text.Encoding)new System.Text.UnicodeEncoding(false, false, true) : new System.Text.UTF8Encoding(false, true)).GetString(bytes.Slice(1, bytes[0] * (wide ? 2 : 1))); }
            catch (System.Text.DecoderFallbackException) { return null; }
        }
        var size = type.ToUpperInvariant() switch { "BOOL" or "SINT" or "USINT" or "BYTE" => 1, "INT" or "UINT" or "WORD" => 2, "DINT" or "UDINT" or "DWORD" or "REAL" => 4, "LINT" or "ULINT" or "LWORD" or "LREAL" or "TIME" or "DATE" or "TOD" or "DT" => 8, _ => 0 };
        if (size == 0 || bytes.Length != size) return null;
        ulong raw = size switch { 1 => bytes[0], 2 => littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes), 4 => littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes), _ => littleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : BinaryPrimitives.ReadUInt64BigEndian(bytes) };
        return type.ToUpperInvariant() switch
        {
            "BOOL" => raw == 0 ? "FALSE" : "TRUE",
            "SINT" => unchecked((sbyte)raw).ToString(CultureInfo.InvariantCulture),
            "INT" => unchecked((short)raw).ToString(CultureInfo.InvariantCulture),
            "DINT" => unchecked((int)raw).ToString(CultureInfo.InvariantCulture),
            "LINT" or "TIME" or "DATE" or "TOD" or "DT" => unchecked((long)raw).ToString(CultureInfo.InvariantCulture),
            "REAL" => BitConverter.Int32BitsToSingle(unchecked((int)raw)).ToString("R", CultureInfo.InvariantCulture),
            "LREAL" => BitConverter.Int64BitsToDouble(unchecked((long)raw)).ToString("R", CultureInfo.InvariantCulture),
            _ => raw.ToString(CultureInfo.InvariantCulture)
        };
    }
}
