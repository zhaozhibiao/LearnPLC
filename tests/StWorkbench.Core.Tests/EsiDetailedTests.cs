using System.Text;
using System.Text.Json;
using StWorkbench.Core;

public static class EsiDetailedTests
{
    public static void Run()
    {
        const string body = """
            <Sm StartAddress="#x1000" DefaultSize="128" ControlByte="#x26" Enable="1" Watchdog="1">MBoxOut</Sm>
            <Sm> MBoxIn </Sm><Sm Watchdog="1">Outputs</Sm><Sm>Inputs</Sm>
            <RxPdo Sm="2" Fixed="1" Mandatory="1"><Index>#x1600</Index><Name>Control</Name><Exclude>#x1601</Exclude><Entry><Index>#x6040</Index><SubIndex>0</SubIndex><BitLen>16</BitLen><DataType>UINT</DataType></Entry></RxPdo>
            <RxPdo><Index>#x1601</Index><Exclude>#x1600</Exclude><Entry><Index>#x607A</Index><SubIndex>0</SubIndex><BitLen>32</BitLen></Entry></RxPdo>
            <TxPdo Sm="3"><Index>#x1A00</Index><Entry><Index>#x6041</Index><SubIndex>0</SubIndex><BitLen>16</BitLen></Entry></TxPdo>
            <Dc><OpMode><Name>DC Sync</Name><AssignActivate>#x0300</AssignActivate><CycleTimeSync0 Factor="1">0</CycleTimeSync0><ShiftTimeSync0>125000</ShiftTimeSync0><CycleTimeSync1 Factor="2">0</CycleTimeSync1><ShiftTimeSync1>-1000</ShiftTimeSync1></OpMode></Dc>
            <Mailbox><CoE><InitCmds><InitCmd CompleteAccess="0"><Transition>PS</Transition><Index>#x6060</Index><SubIndex>0</SubIndex><Data>08</Data></InitCmd></InitCmds></CoE></Mailbox>
            """;
        var xml = Wrap(body);
        var device = Read(xml);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(device));
        Check(json.RootElement.TryGetProperty("Description", out var desc) && desc.ValueKind == JsonValueKind.Object, "detailed ESI metadata retained");
        Check(desc.GetProperty("SourceSha256").GetString() == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(xml))).ToLowerInvariant(), "exact ESI source hash");
        var groups = desc.GetProperty("PdoGroups");
        Check(groups.GetArrayLength() == 3 && groups[0].GetProperty("Index").GetInt32() == 0x1600 && !groups[0].GetProperty("IsInput").GetBoolean(), "PDO groups and direction");
        Check(groups[0].GetProperty("Mandatory").GetBoolean() && groups[0].GetProperty("Fixed").GetBoolean() && groups[0].GetProperty("DefaultSelected").GetBoolean() && !groups[1].GetProperty("DefaultSelected").GetBoolean(), "only assigned PDO defaults selected");
        Check(groups[0].GetProperty("Excludes")[0].GetInt32() == 0x1601 && device.Entries.Count == 3, "exclusions and legacy flattened entries");
        var sm = desc.GetProperty("SyncManagers")[0];
        Check(sm.GetProperty("StartAddress").GetInt32() == 0x1000 && sm.GetProperty("Watchdog").GetBoolean() && sm.GetProperty("DefaultSize").GetInt32() == 128, "sync manager settings");
        var mode = desc.GetProperty("DcModes")[0];
        Check(mode.GetProperty("AssignActivate").GetInt32() == 0x300 && mode.GetProperty("Sync0").GetProperty("Factor").GetInt32() == 1 && mode.GetProperty("Sync0").GetProperty("Shift").GetInt64() == 125000 && mode.GetProperty("Sync1").GetProperty("Shift").GetInt64() == -1000, "DC mode timing");
        var cmd = desc.GetProperty("StartupCommands")[0];
        Check(cmd.GetProperty("Transition").GetString() == "PS" && cmd.GetProperty("Index").GetInt32() == 0x6060 && cmd.GetProperty("DataHex").GetString() == "08", "startup command");
        Check(desc.GetProperty("IsDescriptionSupported").GetBoolean(), "known description accepted");
        Check(JsonSerializer.Deserialize<EtherCatDevice>(JsonSerializer.Serialize(new EtherCatDevice(Guid.NewGuid(), "Old", 1, 2, 3, [])))!.Entries.Count == 0, "legacy record JSON");
        foreach (var data in new[] { "0", "GG", "00 01", "0x01", new string('0', 8194) })
            Reject(Wrap($"<Mailbox><CoE><InitCmds><InitCmd><Transition>PS</Transition><Index>#x6060</Index><SubIndex>0</SubIndex><Data>{data}</Data></InitCmd></InitCmds></CoE></Mailbox>"), "strict bounded startup hex");
        Reject(Wrap("<RxPdo Sm='-1'><Index>#x1600</Index></RxPdo>"), "invalid sync manager");
        using var unknown = JsonDocument.Parse(JsonSerializer.Serialize(Read(Wrap("<Mailbox><SoE/></Mailbox>"))));
        Check(!unknown.RootElement.GetProperty("Description").GetProperty("IsDescriptionSupported").GetBoolean(), "unsupported operational XML is blocked");
        using var conflicting = JsonDocument.Parse(JsonSerializer.Serialize(Read(Wrap("<RxPdo Sm='2'><Index>#x1600</Index><Exclude>#x1601</Exclude></RxPdo><RxPdo Sm='2'><Index>#x1601</Index></RxPdo>"))));
        Check(!conflicting.RootElement.GetProperty("Description").GetProperty("IsDescriptionSupported").GetBoolean(), "conflicting default groups blocked");
        using var duplicate = JsonDocument.Parse(JsonSerializer.Serialize(Read(Wrap("<Mailbox><CoE><InitCmds><InitCmd><Transition>PS</Transition><Index>#x6060</Index><Index>#x6040</Index><SubIndex>0</SubIndex><Data>08</Data></InitCmd></InitCmds></CoE></Mailbox>"))));
        Check(!duplicate.RootElement.GetProperty("Description").GetProperty("IsDescriptionSupported").GetBoolean(), "ambiguous startup fields blocked");
        using var objectEntry = JsonDocument.Parse(JsonSerializer.Serialize(Read(Wrap("<RxPdo Sm='2'><Index>#x1600</Index><Entry UnsupportedMapping='1'><Index>#x6040</Index><SubIndex>0</SubIndex><BitLen>16</BitLen></Entry></RxPdo>"))));
        Check(!objectEntry.RootElement.GetProperty("Description").GetProperty("IsDescriptionSupported").GetBoolean(), "unknown operational attribute blocked");
        var legacyJson = "{\"Id\":\"00000000-0000-0000-0000-000000000001\",\"Name\":\"Old\",\"VendorId\":1,\"ProductCode\":2,\"Revision\":3,\"Entries\":[]}";
        Check(JsonSerializer.Deserialize<EtherCatDevice>(legacyJson)!.Entries.Count == 0, "old JSON without description");
        Console.WriteLine("PASS: detailed ESI groups, SM, DC, startup, provenance, compatibility and rejection");
    }
    private static string Wrap(string body) => "<EtherCATInfo><Vendor><Id>1</Id></Vendor><Descriptions><Devices><Device><Type ProductCode='2' RevisionNo='3'>Servo</Type>" + body + "</Device></Devices></Descriptions></EtherCATInfo>";
    private static EtherCatDevice Read(string xml) { using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml)); return EsiImporter.Read(stream).Single(); }
    private static void Reject(string xml, string name) { try { Read(xml); } catch (InvalidDataException) { return; } throw new Exception("FAIL: " + name); }
    private static void Check(bool ok, string name) { if (!ok) throw new Exception("FAIL: " + name); }
}
