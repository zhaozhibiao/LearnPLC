using System.Text.Json;
namespace StWorkbench.App;
public sealed record WorkbenchSettings(string Endpoint = "https://localhost:8443", string Username = "a", string CompilerPath = "", string CompilerVersion = "STruC++ version 0.7.0", string CertificateFingerprint = "")
{
    public static string WorkspaceRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            return AppContext.BaseDirectory;
        }
    }
    private static string SettingsPath => Path.Combine(WorkspaceRoot, "artifacts", "user-settings.json");
    public static WorkbenchSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath)) return JsonSerializer.Deserialize<WorkbenchSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        return new(CompilerPath: Path.Combine(WorkspaceRoot, ".tools", "strucpp-0.7.0", "strucpp", "strucpp.exe"));
    }
    public void Save()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0) throw new InvalidDataException("Runtime 地址必须是 HTTPS 主机地址");
        if (string.IsNullOrWhiteSpace(Username)) throw new InvalidDataException("请输入 Runtime 用户名");
        if (!File.Exists(CompilerPath)) throw new InvalidDataException("找不到 ST 编译器，请选择独立的 strucpp.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temp = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, SettingsPath, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
