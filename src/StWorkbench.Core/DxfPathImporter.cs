using System.Globalization;
using System.Text;

namespace StWorkbench.Core;

public sealed record DxfContour(string Name, string Layer, PathSegment[] Segments);

/// <summary>Bounded ASCII DXF importer adapted from EcMotion.Sdk.CadPathImporter.
/// Retains line/XY arc geometry and world XYZ coordinates; never submits device commands.</summary>
public static class DxfPathImporter
{
    public const int MaximumBytes = 8 * 1024 * 1024;
    public const int MaximumSegments = 100_000;
    public static IReadOnlyList<DxfContour> Parse(string text, double millimetersPerUnit = 1)
    {
        if (text is null || text.Length > MaximumBytes || Encoding.UTF8.GetByteCount(text) > MaximumBytes || text.Contains('\0'))
            throw new InvalidDataException("DXF must be bounded ASCII text (8 MiB maximum).");
        if (!double.IsFinite(millimetersPerUnit) || millimetersPerUnit <= 0 || millimetersPerUnit > 1000) throw new InvalidDataException("Invalid DXF scale.");
        var tags = new List<(int Code, string Value)>();
        using var reader = new StringReader(text);
        while (reader.ReadLine() is string code) {
            var value = reader.ReadLine();
            if (value is null || !int.TryParse(code.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) throw new InvalidDataException("Incomplete DXF group pair.");
            tags.Add((number, value.Trim()));
        }
        var contours = new List<DxfContour>(); int segmentCount = 0;
        bool entities = false, found = false, ended = false, closed = false;
        List<PathPoint>? poly = null; string polyLayer = "0";
        double Number(List<(int Code, string Value)> f, int code, double? fallback = null) {
            var values = f.Where(x => x.Code == code).ToArray();
            if (values.Length == 0 && fallback is double d) return d;
            if (values.Length != 1 || !double.TryParse(values[0].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || Math.Abs(n) > 1e9) throw new InvalidDataException($"Invalid DXF number {code}.");
            return n;
        }
        PathPoint Point(List<(int Code, string Value)> f, int code) => Bounded(new(Number(f, code) * millimetersPerUnit, Number(f, code + 10) * millimetersPerUnit, Number(f, code + 20, 0) * millimetersPerUnit));
        int Flags(List<(int Code, string Value)> f) {
            double value = Number(f, 70, 0);
            if (value != Math.Truncate(value) || value < 0 || value > int.MaxValue) throw new InvalidDataException("DXF flags must be nonnegative integers.");
            return (int)value;
        }
        void Add(string layer, PathSegment[] segments) {
            if (contours.Count >= 2048 || segmentCount + segments.Length > MaximumSegments) throw new InvalidDataException("DXF geometry budget exceeded.");
            segmentCount += segments.Length; contours.Add(new($"Contour{contours.Count + 1}", layer, segments));
        }
        PathSegment[] Lines(List<PathPoint> vertices, bool close) {
            if (vertices.Count < 2) throw new InvalidDataException("Polyline needs two vertices.");
            var segments = new List<PathSegment>();
            for (int j = 1; j < vertices.Count; j++) segments.Add(new("Line", vertices[j - 1], vertices[j]));
            if (close && vertices[^1] != vertices[0]) segments.Add(new("Line", vertices[^1], vertices[0]));
            return segments.ToArray();
        }
        for (int i = 0; i < tags.Count;) {
            if (tags[i].Code != 0) { i++; continue; }
            string kind = tags[i++].Value;
            var f = new List<(int Code, string Value)>();
            while (i < tags.Count && tags[i].Code != 0) f.Add(tags[i++]);
            if (kind == "SECTION") { if (entities || poly is not null) throw new InvalidDataException("Nested DXF section."); entities = f.Any(x => x.Code == 2 && x.Value == "ENTITIES"); found |= entities; continue; }
            if (kind == "ENDSEC") { if (poly is not null) throw new InvalidDataException("Missing SEQEND."); entities = false; continue; }
            if (kind == "EOF") { if (i != tags.Count) throw new InvalidDataException("Data after EOF."); ended = true; break; }
            if (!entities) continue;
            if (poly is not null && kind is not "VERTEX" and not "SEQEND") throw new InvalidDataException("Incomplete POLYLINE.");
            string layer = f.FirstOrDefault(x => x.Code == 8).Value ?? "0";
            if (Number(f, 210, 0) != 0 || Number(f, 220, 0) != 0 || Number(f, 230, 1) != 1 || Number(f, 39, 0) != 0) throw new InvalidDataException("Nondefault normal/thickness is unsupported.");
            switch (kind) {
                case "LINE": Add(layer, [new("Line", Point(f, 10), Point(f, 11))]); break;
                case "ARC": case "CIRCLE":
                    var center = Point(f, 10); double radius = Number(f, 40) * millimetersPerUnit;
                    if (radius <= 0 || !double.IsFinite(radius)) throw new InvalidDataException("Invalid radius.");
                    double start = kind == "CIRCLE" ? 0 : Number(f, 50), end = kind == "CIRCLE" ? 360 : Number(f, 51);
                    double sweep = kind == "CIRCLE" ? 360 : ((end - start) % 360 + 360) % 360;
                    if (sweep == 0) throw new InvalidDataException("Zero arc sweep.");
                    PathPoint At(double a) => Bounded(new(center.X + radius * Math.Cos(a * Math.PI / 180), center.Y + radius * Math.Sin(a * Math.PI / 180), center.Z));
                    var first = At(start); Add(layer, [new("Arc", first, kind == "CIRCLE" ? first : At(start + sweep), center, sweep * Math.PI / 180)]); break;
                case "POLYLINE":
                    int flags = Flags(f);
                    if ((flags & ~9) != 0 || ((flags & 8) == 0 && Number(f, 30, 0) != 0)) throw new InvalidDataException("Unsupported POLYLINE flags/elevation.");
                    poly = []; polyLayer = layer; closed = (flags & 1) != 0; break;
                case "VERTEX":
                    if (poly is null || Number(f, 42, 0) != 0 || (Flags(f) & ~32) != 0) throw new InvalidDataException("Unsupported standalone/bulge/fitted vertex.");
                    if (poly.Count >= MaximumSegments) throw new InvalidDataException("Polyline budget exceeded.");
                    poly.Add(Point(f, 10)); break;
                case "SEQEND":
                    if (poly is null) throw new InvalidDataException("Unexpected SEQEND.");
                    Add(polyLayer, Lines(poly, closed)); poly = null; break;
                case "LWPOLYLINE":
                    if (f.Any(x => x.Code == 42 && (!double.TryParse(x.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) || b != 0))) throw new InvalidDataException("Bulge unsupported; export ARC.");
                    int lwFlags = Flags(f);
                    if ((lwFlags & ~1) != 0) throw new InvalidDataException("Unsupported lightweight polyline flags.");
                    double z = Number(f, 38, 0) * millimetersPerUnit; var vertices = new List<PathPoint>();
                    for (int j = 0; j < f.Count; j++) if (f[j].Code == 10) {
                        var v = new List<(int Code, string Value)> { f[j] };
                        while (j + 1 < f.Count && f[j + 1].Code != 10) v.Add(f[++j]);
                        if (vertices.Count >= MaximumSegments) throw new InvalidDataException("Polyline budget exceeded.");
                        vertices.Add(Bounded(new(Number(v, 10) * millimetersPerUnit, Number(v, 20) * millimetersPerUnit, z)));
                    }
                    if (Number(f, 90) != vertices.Count) throw new InvalidDataException("Polyline vertex count mismatch.");
                    Add(layer, Lines(vertices, (lwFlags & 1) != 0)); break;
                default: throw new InvalidDataException($"Unsupported DXF entity {kind}; no partial drawing imported.");
            }
        }
        if (!found || !ended || entities || poly is not null || contours.Count == 0) throw new InvalidDataException("Incomplete or empty DXF ENTITIES/ENDSEC/EOF.");
        return contours.AsReadOnly();
    }
    static PathPoint Bounded(PathPoint p) {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Z) || Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))) > 1e6) throw new InvalidDataException("DXF coordinate outside ±1000000 mm.");
        return p;
    }
}
