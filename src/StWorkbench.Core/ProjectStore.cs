using System.Text;
using System.Text.Json;
namespace StWorkbench.Core;
public sealed record LoadedProject(StProject Project, IReadOnlyList<SourceDocument> Sources);
public sealed class ProjectStore
{
    private sealed record ProjectFile(int SchemaVersion, StProject Project, Dictionary<Guid, string> SourceFiles);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 64 };
    public async Task SaveAsync(string path, StProject project, IReadOnlyList<SourceDocument> sources, CancellationToken token = default)
    {
        var errors = ProjectValidator.Validate(project, sources);
        if (errors.Count > 0) throw new InvalidDataException(string.Join(Environment.NewLine, errors.Select(e => e.Message)));
        // Freeze all manifest metadata before asynchronous IO lets the editor resume.
        project = JsonSerializer.Deserialize<StProject>(JsonSerializer.Serialize(project, Json), Json)
            ?? throw new InvalidDataException("空工程");
        var snapshot = ProjectValidator.Capture(project, sources);
        var manifestPath = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(manifestPath)!;
        Directory.CreateDirectory(root);
        CheckLinks(root, manifestPath);
        var revision = $".stversions/{snapshot.SourceHash}/{snapshot.HardwareHash}";
        var files = new Dictionary<Guid, string>();
        foreach (var source in snapshot.Sources)
        {
            var relative = revision + "/" + source.RelativePath.Replace('\\', '/');
            var target = Resolve(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CheckLinks(root, target);
            if (File.Exists(target))
            {
                if (await File.ReadAllTextAsync(target, token) != source.Text) throw new InvalidDataException("已有源版本内容不匹配，拒绝覆盖");
            }
            else
            {
                await AtomicWrite(target, source.Text, false, token);
            }
            files.Add(source.ItemId, relative);
        }
        // Immutable source revision first, manifest last. A failed save leaves the previous manifest usable.
        var payload = JsonSerializer.Serialize(new ProjectFile(1, project, files), Json);
        if (Encoding.UTF8.GetByteCount(payload) > 4 * 1024 * 1024) throw new InvalidDataException("工程清单过大，请拆分工程");
        await AtomicWrite(manifestPath, payload, true, token);
    }
    public async Task<LoadedProject> LoadAsync(string path, CancellationToken token = default)
    {
        var manifestPath = Path.GetFullPath(path);
        var root = Path.GetDirectoryName(manifestPath)!;
        CheckLinks(root, manifestPath);
        if (new FileInfo(manifestPath).Length > 4 * 1024 * 1024) throw new InvalidDataException("工程清单过大");
        ProjectFile file;
        try { file = JsonSerializer.Deserialize<ProjectFile>(await File.ReadAllTextAsync(manifestPath, token), Json) ?? throw new InvalidDataException("空工程"); }
        catch (JsonException e) { throw new InvalidDataException("工程清单格式错误", e); }
        if (file.SchemaVersion != 1 || !ProjectValidator.HasValidStructure(file.Project) || file.SourceFiles is null) throw new InvalidDataException("工程清单结构无效");
        // Validate schema, metadata and nominal paths before opening any referenced source.
        _ = ProjectValidator.Capture(file.Project, file.Project.Items.Select(i => new SourceDocument(i.Id, i.SourcePath, "")).ToArray());
        var docs = new List<SourceDocument>();
        foreach (var item in file.Project.Items)
        {
            if (!file.SourceFiles.TryGetValue(item.Id, out var relative)) throw new InvalidDataException("工程源文件清单缺失");
            var target = Resolve(root, relative);
            if (new FileInfo(target).Length > 16 * 1024 * 1024) throw new InvalidDataException("源文件过大");
            docs.Add(new(item.Id, item.SourcePath, await File.ReadAllTextAsync(target, token)));
        }
        if (file.SourceFiles.Count != docs.Count) throw new InvalidDataException("工程包含多余源文件引用");
        _ = ProjectValidator.Capture(file.Project, docs);
        return new(file.Project, docs.AsReadOnly());
    }
    public static string Resolve(string root, string relative)
    {
        if (!ProjectValidator.IsSafeRelativePath(relative)) throw new InvalidDataException("路径必须位于工程目录内");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("工程路径越界");
        CheckLinks(root, full);
        return full;
    }
    private static void CheckLinks(string root, string path)
    {
        var current = Path.GetFullPath(path);
        var boundary = Path.GetFullPath(root);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("工程路径不能经过符号链接或目录联接");
            if (string.Equals(current, boundary, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("工程路径边界错误");
        }
    }
    private static async Task AtomicWrite(string path, string text, bool overwrite, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await stream.WriteAsync(bytes, token);
                await stream.FlushAsync(token);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
