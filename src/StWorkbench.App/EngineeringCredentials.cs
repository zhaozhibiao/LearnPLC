using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace StWorkbench.App;

internal sealed class EngineeringCredentials
{
    public string Endpoint { get; set; } = "";
    public string Token { get; set; } = "";
    public string CertificateSha256 { get; set; } = "";
    public override string ToString() => "Engineering device credentials (protected)";
}

/// <summary>Pairing secrets live in Windows Credential Manager, never project/settings JSON.</summary>
internal static class EngineeringCredentialStore
{
    private static string Target(string host, string user) => "StWorkbench/Engineering/" + user + "@" + host;
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint="CredWriteW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Write(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint="CredReadW", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Read(string target,uint type,uint flags,out IntPtr credential);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    public static EngineeringCredentials? Load(string host,string user)
    {
        if (!OperatingSystem.IsWindows() || !Read(Target(host,user),1,0,out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize is 0 or > 16384) return null;
            var bytes = new byte[credential.CredentialBlobSize]; Marshal.Copy(credential.CredentialBlob,bytes,0,bytes.Length);
            try { return JsonSerializer.Deserialize<EngineeringCredentials>(bytes); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (JsonException) { return null; }
        finally { CredFree(pointer); }
    }
    public static void Save(string host,string user,EngineeringCredentials value)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Persistent pairing requires the platform credential store.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value); var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes,0,pointer,bytes.Length);
            var credential = new Credential { Type=1,TargetName=Target(host,user),UserName=user,CredentialBlob=pointer,CredentialBlobSize=(uint)bytes.Length,Persist=2 };
            if (!Write(ref credential,0)) throw new InvalidOperationException("无法保存设备配对凭据，请检查 Windows 凭据管理器。");
        }
        finally { for(var i=0;i<bytes.Length;i++) Marshal.WriteByte(pointer,i,0); Marshal.FreeHGlobal(pointer); System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
    }
}
