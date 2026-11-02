using System.Text;
using StWorkbench.Core;
public static class EsiTests
{
    public static void Run()
    {
        EsiDetailedTests.Run();
        const string xml = "<EtherCATInfo><Vendor><Id>#x00000002</Id><Name>Vendor</Name></Vendor><Descriptions><Devices><Device><Type ProductCode='#x1234' RevisionNo='#x10'>EL Example</Type><Name>IO</Name><TxPdo><Index>#x1A00</Index><Entry><Index>#x6000</Index><SubIndex>1</SubIndex><BitLen>1</BitLen><Name>Input</Name><DataType>BOOL</DataType></Entry></TxPdo><RxPdo><Index>#x1600</Index><Entry><Index>#x7000</Index><SubIndex>1</SubIndex><BitLen>16</BitLen><Name>Output</Name><DataType>INT</DataType></Entry></RxPdo></Device></Devices></Descriptions></EtherCATInfo>";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var devices = EsiImporter.Read(input);
        if (devices.Count != 1 || devices[0].VendorId != 2 || devices[0].ProductCode != 0x1234 || devices[0].Entries.Count != 2 || !devices[0].Entries[0].IsInput) throw new Exception("FAIL: ESI device/IO direction");
        using var malicious = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY ext SYSTEM 'file:///C:/Windows/win.ini'>]><EtherCATInfo>&ext;</EtherCATInfo>"));
        try { EsiImporter.Read(malicious); throw new Exception("FAIL: external XML entity accepted"); } catch (InvalidDataException) { }
        Console.WriteLine("PASS: ESI identifiers, PDO entry metadata, direction, external entity rejection");
    }
}
