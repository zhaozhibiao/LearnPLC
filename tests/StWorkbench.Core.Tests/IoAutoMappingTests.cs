using System.Text.Json;
using System.Text.RegularExpressions;
using StWorkbench.Core;

public static class IoAutoMappingTests
{
    public static void Run()
    {
        MixedTypes();
        ReservationsAndNames();
        LimitsAndInvalidAddresses();
        Console.WriteLine("PASS: automatic IO types, alignment, reservations, names, idempotence, bounds and atomic failure");
    }

    private static void MixedTypes()
    {
        var types = new[] { ("BOOLEAN", 1, "BOOL", "%IX0.0"), ("BOOL", 1, "BOOL", "%IX0.1"),
            ("UINT8", 8, "BYTE", "%IB1"), ("UINT16", 16, "WORD", "%IW1"),
            ("INT16", 16, "INT", "%IW2"), ("UINT32", 32, "DWORD", "%ID2"),
            ("INT32", 32, "DINT", "%ID3"), ("REAL32", 32, "REAL", "%ID4"),
            ("REAL64", 64, "LREAL", "%IL3") };
        var entries = types.Select((t, i) => Entry((ushort)(0x6000 + i), t.Item1, t.Item2)).ToList();
        entries.Add(Entry(0x7000, "BOOL", 1, false));
        entries.Add(Entry(0x6010, "STRING", 80));
        entries.Add(Entry(0x6011, "INT", 32));
        var station = Station(entries);
        var hardware = new HardwareConfiguration();
        var before = JsonSerializer.Serialize(hardware);
        var plan = IoAutoMapping.Plan(hardware, station);
        Check(JsonSerializer.Serialize(hardware) == before, "planning must not mutate hardware");
        Check(plan.Added.Count == 10 && plan.Skipped.Count == 2, "unsupported entries skipped");
        for (var i = 0; i < types.Length; i++)
            Check(plan.Added[i].Type == types[i].Item3 && plan.Added[i].Address == types[i].Item4, "mixed allocation " + i);
        Check(plan.Added[9].Address == "%QX0.0", "independent output range");
        Check(plan.Skipped.Any(s => s.Contains("6010") && s.Contains("STRING")) &&
              plan.Skipped.Any(s => s.Contains("6011") && s.Contains("INT")), "skips explain PDO and type");
        Check(plan.Added.All(io => io.StationId == station.Id && io.PdoSubIndex == 1), "station and PDO references");
        hardware.Io.AddRange(plan.Added);
        Check(IoAutoMapping.Plan(hardware, station).Added.Count == 0, "applied plan is idempotent");
        var supported = station with { EnabledEntries = entries.Take(10).ToArray() };
        Check(IoAutoMapping.Plan(hardware, supported).Skipped.Count == 0, "already mapped supported entries produce no warnings");
        var duplicate = Entry(0x6100, "BOOL", 1);
        var duplicatePlan = IoAutoMapping.Plan(new(), Station([duplicate, duplicate]));
        Check(duplicatePlan.Added.Count == 1 && duplicatePlan.Skipped.Count == 0, "pending duplicate entries deduplicate without warnings");
        var second = IoAutoMapping.Plan(hardware, Station([Entry(0x6000, "BOOL", 1)]));
        Check(second.Added.Single().Address == "%IX0.2", "second station uses remaining bit");
        var project = new StProject { Hardware = new HardwareConfiguration { Io = [new("Large", "%IL0", "LREAL", true)] } };
        Check(!ProjectValidator.Validate(project, []).Any(d => d.Message.Contains("IO 地址")), "L address passes project address validation");
    }

    private static void ReservationsAndNames()
    {
        var station = Station(Enumerable.Range(0, 10).Select(i => Entry((ushort)(0x6000 + i), "BOOL", 1)).ToArray()) with { Name = "9中文-" + new string('a', 90) };
        var hardware = new HardwareConfiguration { Io = [new("manual", "%id000", "LREAL", true), new("alias", "%IB0000", "BYTE", true)] };
        var before = JsonSerializer.Serialize(hardware);
        var plan = IoAutoMapping.Plan(hardware, station);
        Check(plan.Added[0].Address == "%IX8.0" && plan.Added[8].Address == "%IX9.0", "legacy wider type reserves 64 bits and bits pack");
        Check(plan.Added.All(io => io.Name.Length <= 63 && Regex.IsMatch(io.Name, "^[A-Za-z_][A-Za-z0-9_]*$")), "bounded ASCII names");
        Check(JsonSerializer.Serialize(hardware) == before, "existing addresses preserved exactly");
        var baseName = plan.Added[0].Name;
        hardware.Axes.Add(new(baseName.ToUpperInvariant(), "Axis1", "mm", 0, 1));
        hardware.Io.Add(new(baseName + "_2", "%QX0.0", "BOOL", false));
        var renamed = IoAutoMapping.Plan(hardware, station).Added[0].Name;
        Check(renamed.Length <= 63 && !renamed.Equals(baseName, StringComparison.OrdinalIgnoreCase) && !renamed.Equals(baseName + "_2", StringComparison.OrdinalIgnoreCase), "IO and axis name collision avoidance");
        var equivalent = new HardwareConfiguration { Io = [new("first", "%ix000.00", "BOOL", true), new("second", "%IX0.0", "BOOL", true)] };
        Check(IoAutoMapping.Plan(equivalent, Station([Entry(1, "BOOL", 1)])).Added[0].Address == "%IX0.1", "normalized case and leading zeros");
    }

    private static void LimitsAndInvalidAddresses()
    {
        var station = Station([Entry(1, "BOOL", 1)]);
        foreach (var address in new[] { "%IW0.1", "%IX0.8", "%IB65536", "%IW32768", "%ID16384", "%IL8192", "%IB99999999999999999999999", "%QX0.0", "bad", "%IX0" })
        {
            var hardware = new HardwareConfiguration { Io = [new("manual", address, "BOOL", true)] };
            Reject(() => IoAutoMapping.Plan(hardware, station), address);
            Check(hardware.Io.Count == 1, "invalid allocation remains unchanged");
        }
        var filled = new HardwareConfiguration { Io = Enumerable.Range(0, 8192).Select(i => new IoMapping("n" + i, "%IL" + i, "LREAL", true)).ToList() };
        Reject(() => IoAutoMapping.Plan(filled, station), "full address space");
        filled.Io.RemoveAt(filled.Io.Count - 1);
        var last = Station([Entry(1, "LREAL", 64), Entry(2, "BOOL", 1)]);
        Reject(() => IoAutoMapping.Plan(filled, last), "failure after pending allocation");
        Check(filled.Io.Count == 8191, "failed plan is atomic");
        Check(IoAutoMapping.Plan(filled, Station([Entry(1, "LREAL", 64)])).Added.Single().Address == "%IL8191", "last legal L address");
        var edge = new HardwareConfiguration { Io = [new("last", "%IX65535.7", "BOOL", true)] };
        Check(IoAutoMapping.Plan(edge, station).Added.Single().Address == "%IX0.0", "maximum bit address accepted");
        Reject(() => IoAutoMapping.Plan(new() { Io = [new("overflow", "%ID16383", "LREAL", true)] }, station), "wider legacy span crosses bound");
    }

    private static PdoEntry Entry(ushort index, string type, int width, bool input = true) => new(index, 1, width, "entry", type, input);
    private static EtherCatStation Station(IReadOnlyList<PdoEntry> entries) => new(Guid.NewGuid(), "Station", Guid.NewGuid(), 0, entries);
    private static void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); }
    private static void Reject(Action action, string name) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("FAIL: accepted " + name); }
}
