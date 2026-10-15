using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StWorkbench.Core;

namespace StWorkbench.Mcp;

public sealed class WorkspaceService
{
    private sealed class Draft(string path, StProject project, SourceDocument[] sources, string? baseline)
    {
        public string Path { get; } = path;
        public StProject Project { get; set; } = project;
        public SourceDocument[] Sources { get; set; } = sources;
        public string? Baseline { get; set; } = baseline;
        public string? SavedRevision { get; set; }
        public string Revision => ProjectValidator.Hash(JsonSerializer.Serialize(new { Project, Sources }, McpJson.Options));
    }
    private readonly Dictionary<string, Draft> drafts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    private readonly ProjectStore store = new();
    public string Root { get; }
    public WorkspaceService(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root)) throw new ArgumentException("Workspace directory must already exist.");
        CheckAncestors(Root);
    }
    public string Resolve(string relative) => ProjectStore.Resolve(Root, relative);
    private string ManifestPath(string relative)
    {
        if (!relative.EndsWith(".stproj", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Expected workspace-relative .stproj path.");
        return Resolve(relative);
    }
    public async Task<WorkspaceView> CreateAsync(string name, string relative)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("Project name must be 1..256 characters.");
        var full = ManifestPath(relative);
        await gate.WaitAsync();
        try
        {
            if (File.Exists(full)) throw new IOException("Project already exists; open it instead.");
            var item = new ProjectItem();
            var project = new StProject { Name = name, Items = [item], Tasks = [new() { ProgramId = item.Id }] };
            return Add(relative, project, [new(item.Id, item.SourcePath, "PROGRAM Main\nVAR\n    counter : DINT := 0;\nEND_VAR\n\nEND_PROGRAM\n")], null);
        }
        finally { gate.Release(); }
    }
    public async Task<WorkspaceView> OpenAsync(string relative)
    {
        var path = ManifestPath(relative);
        await gate.WaitAsync();
        try
        {
            var baseline = await DiskHash(path);
            var loaded = await store.LoadAsync(path);
            if (await DiskHash(path) != baseline) throw new IOException("Project changed while opening; open again.");
            var view = Add(relative, loaded.Project, loaded.Sources.ToArray(), baseline);
            drafts[view.SessionId].SavedRevision = view.Revision;
            return view with { HasUnsavedChanges = false };
        }
        finally { gate.Release(); }
    }
    private WorkspaceView Add(string path, StProject project, SourceDocument[] sources, string? baseline)
    {
        if (drafts.Count >= 16) throw new InvalidOperationException("Session capacity reached (16); close an unused project draft.");
        CheckBudget(project, sources);
        var id = Guid.NewGuid().ToString("N");
        var draft = new Draft(path, McpJson.Clone(project), McpJson.Clone(sources), baseline);
        drafts.Add(id, draft);
        return View(id, draft);
    }
    public async Task<WorkspaceView> ReadAsync(string sessionId)
    {
        await gate.WaitAsync();
        try { return View(sessionId, Get(sessionId)); }
        finally { gate.Release(); }
    }
    public async Task CloseAsync(string sessionId)
    {
        await gate.WaitAsync();
        try { if (!drafts.Remove(sessionId)) throw new ArgumentException("Unknown project session."); }
        finally { gate.Release(); }
    }
    public Task<WorkspaceView> WriteSourceAsync(string session, string revision, Guid itemId, string text) =>
        EditAsync(session, revision, (project, sources) =>
        {
            var at = sources.FindIndex(s => s.ItemId == itemId);
            if (at < 0) throw new ArgumentException("Unknown source item.");
            sources[at] = sources[at] with { Text = text };
        }, false);

    public Task<WorkspaceView> ReplaceAsync(string session, string revision, StProject project, SourceDocument[] sources) =>
        EditAsync(session, revision, (current, docs) =>
        {
            if (current.Id != project.Id) throw new InvalidDataException("Project identity cannot be changed.");
            foreach (var old in current.Paths)
                if (!project.Paths.Any(p => p.Id == old.Id && p.Version == old.Version && JsonSerializer.Serialize(p) == JsonSerializer.Serialize(old)))
                    throw new InvalidDataException("Existing path versions are immutable; append a new version instead.");
            var copied = McpJson.Clone(project);
            current.Name = copied.Name; current.SchemaVersion = copied.SchemaVersion; current.Items = copied.Items;
            current.Tasks = copied.Tasks; current.Hardware = copied.Hardware; current.Network = copied.Network;
            current.MotionCycle = copied.MotionCycle; current.Paths = copied.Paths;
            docs.Clear(); docs.AddRange(McpJson.Clone(sources));
        }, false);

    public async Task<WorkspaceView> EditAsync(string session, string revision, Action<StProject, List<SourceDocument>> edit, bool validate = true)
    {
        await gate.WaitAsync();
        try
        {
            var draft = Get(session, revision);
            var project = McpJson.Clone(draft.Project); var docs = McpJson.Clone(draft.Sources).ToList();
            edit(project, docs);
            CheckBudget(project, docs);
            CheckPathAppend(draft.Project.Paths, project.Paths);
            if (validate) RequireValid(project, docs);
            draft.Project = project; draft.Sources = docs.ToArray();
            return View(session, draft);
        }
        finally { gate.Release(); }
    }
    public async Task<WorkspaceView> SaveAsync(string session, string revision)
    {
        await gate.WaitAsync();
        try
        {
            var draft = Get(session, revision);
            RequireValid(draft.Project, draft.Sources);
            var path = ManifestPath(draft.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _ = ManifestPath(draft.Path);
            // Cooperating MCP servers serialize, then compare the saved baseline inside the lock.
            await using var writeLock = new FileStream(path + ".mcp-lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (await DiskHash(path) != draft.Baseline) throw new IOException("Saved project changed outside this draft. Reopen and merge changes.");
            await store.SaveAsync(path, draft.Project, draft.Sources);
            draft.Baseline = await DiskHash(path); draft.SavedRevision = draft.Revision;
            return View(session, draft);
        }
        finally { gate.Release(); }
    }
    public Task<WorkspaceView> AddResourceAsync(string session, string revision, string name, ItemKind kind, string? text = null) =>
        EditAsync(session, revision, (project, sources) =>
        {
            RequireName(name);
            if (!Enum.IsDefined(kind) || project.Items.Any(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Resource kind invalid or name already exists.");
            var item = new ProjectItem { Name = name, Kind = kind, SourcePath = "sources/" + name + ".st" };
            var body = kind switch
            {
                ItemKind.Program => $"PROGRAM {name}\nVAR\nEND_VAR\n\nEND_PROGRAM\n",
                ItemKind.Function => $"FUNCTION {name} : BOOL\nVAR_INPUT\nEND_VAR\n{name} := FALSE;\nEND_FUNCTION\n",
                ItemKind.FunctionBlock => $"FUNCTION_BLOCK {name}\nVAR_INPUT\nEND_VAR\nVAR_OUTPUT\nEND_VAR\n\nEND_FUNCTION_BLOCK\n",
                ItemKind.DataType => $"TYPE {name} : STRUCT\n    Value : DINT;\nEND_STRUCT;\nEND_TYPE\n",
                _ => "VAR_GLOBAL\nEND_VAR\n"
            };
            project.Items.Add(item); sources.Add(new(item.Id, item.SourcePath, text ?? body));
        });
    public Task<WorkspaceView> DeleteResourceAsync(string session, string revision, Guid itemId) =>
        EditAsync(session, revision, (project, sources) =>
        {
            var item = project.Items.SingleOrDefault(i => i.Id == itemId) ?? throw new ArgumentException("Unknown resource.");
            if (project.Tasks.Any(t => t.ProgramId == itemId)) throw new InvalidOperationException("Resource is bound to a task. Update tasks atomically before deleting.");
            if (sources.Where(s => s.ItemId != itemId).Any(s => HasIdentifier(s.Text, item.Name)))
                throw new InvalidOperationException("Resource has references; remove or update them before deleting.");
            if (item.Kind == ItemKind.GlobalVariables) throw new InvalidOperationException("Delete global variable resources using an atomic project update after checking member references.");
            project.Items.Remove(item); sources.RemoveAll(s => s.ItemId == itemId);
        });
    public async Task<RenamePreview> PreviewRenameAsync(string session, string revision, Guid itemId, string name)
    {
        var view = await ReadAsync(session);
        if (view.Revision != revision) throw new InvalidOperationException("Draft revision conflict.");
        return Rename(view.Project, view.Sources.ToList(), itemId, name, revision, false);
    }
    public Task<WorkspaceView> RenameResourceAsync(string session, string revision, Guid itemId, string name) =>
        EditAsync(session, revision, (project, sources) => Rename(project, sources, itemId, name, revision, true));
    private static RenamePreview Rename(StProject project, List<SourceDocument> sources, Guid itemId, string name, string revision, bool apply)
    {
        RequireName(name);
        var item = project.Items.SingleOrDefault(i => i.Id == itemId) ?? throw new ArgumentException("Unknown resource.");
        var old = item.Name;
        if (project.Items.Any(i => i.Id != itemId && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Resource name collision.");
        var symbols = new StLanguageService().Index(sources);
        if (symbols.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
            item.Kind != ItemKind.GlobalVariables && symbols.Any(s => s.Name.Equals(old, StringComparison.OrdinalIgnoreCase) && (s.Kind.StartsWith("VAR", StringComparison.Ordinal) || s.Kind == "STRUCT")))
            throw new InvalidOperationException("Rename is ambiguous with an existing symbol; use an atomic reviewed project update.");
        var changed = item.Kind == ItemKind.GlobalVariables ? [] : sources.Select(s => s with { Text = new StLanguageService().RenameLocalIdentifier(s.Text, old, name) }).Where(s => s.Text != sources.Single(d => d.ItemId == s.ItemId).Text).ToArray();
        if (apply)
        {
            item.Name = name;
            foreach (var source in changed) sources[sources.FindIndex(s => s.ItemId == source.ItemId)] = source;
        }
        return new(revision, old, name, changed, "Token rename preserves comments/strings. Review all changed sources; GVL resource labels do not rename members.");
    }
    public static void RequireName(string name)
    {
        if (name is null || name.Length > 128 || !Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*\z") || StLanguageService.Keywords.Contains(name, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Expected a non-keyword ASCII ST identifier (1..128 characters).");
    }
    public static void RequireValid(StProject project, IReadOnlyList<SourceDocument> sources)
    {
        var diagnostics = ProjectValidator.Validate(project, sources);
        if (diagnostics.Count > 0) throw new InvalidDataException(string.Join("\n", diagnostics.Take(32).Select(d => $"{d.File}:{d.Line}:{d.Column} {d.Message}")));
    }
    private Draft Get(string id, string? revision = null)
    {
        if (!drafts.TryGetValue(id, out var draft)) throw new ArgumentException("Unknown project session.");
        if (revision is not null && draft.Revision != revision) throw new InvalidOperationException("Draft revision conflict; read the latest draft and merge.");
        return draft;
    }
    private static WorkspaceView View(string id, Draft draft) => new(id, draft.Path, draft.Revision, draft.Revision != draft.SavedRevision, McpJson.Clone(draft.Project), McpJson.Clone(draft.Sources));
    private static void CheckBudget(StProject project, IReadOnlyList<SourceDocument> sources)
    {
        if (project is null || sources is null || project.Items is null || sources.Count > 256 || sources.Any(s => s is null || s.Text is null || s.RelativePath is null)) throw new InvalidDataException("Invalid project draft structure or source count (maximum 256).");
        var text = JsonSerializer.Serialize(new { project, sources }, McpJson.Options);
        if (Encoding.UTF8.GetByteCount(text) > 12 * 1024 * 1024) throw new InvalidDataException("Project draft exceeds 12 MiB.");
        foreach (var source in sources)
            if (!ProjectValidator.IsSafeRelativePath(source.RelativePath) || !source.RelativePath.EndsWith(".st", StringComparison.OrdinalIgnoreCase) || Encoding.UTF8.GetByteCount(source.Text) > 1024 * 1024) throw new InvalidDataException("Invalid source path or text exceeds 1 MiB.");
        // Return diagnostics for incomplete drafts, but never accept null structures that break tools.
        var errors = ProjectValidator.Validate(project, sources);
        if (errors.Any(e => e.Message == "工程包含空对象、集合或必填字段")) throw new InvalidDataException(errors[0].Message);
    }
    private static async Task<string?> DiskHash(string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Project manifest exceeds 4 MiB.");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }
    private static bool HasIdentifier(string text, string name) => new StLanguageService().RenameLocalIdentifier(text, name, "McpReferenceProbe_" + Guid.NewGuid().ToString("N")) != text;
    private static void CheckPathAppend(IReadOnlyList<MotionPathAsset> old, IReadOnlyList<MotionPathAsset> next)
    {
        foreach (var previous in old)
            if (!next.Any(p => p.Id == previous.Id && p.Version == previous.Version && JsonSerializer.Serialize(p) == JsonSerializer.Serialize(previous)))
                throw new InvalidDataException("Existing path versions cannot be changed or removed.");
        foreach (var added in next.Where(p => !old.Any(o => o.Id == p.Id && o.Version == p.Version)))
        {
            var previous = old.Where(p => p.Id == added.Id).ToArray();
            if (previous.Any(p => p.Name != added.Name) || previous.Length > 0 && added.Version <= previous.Max(p => p.Version))
                throw new InvalidDataException("New path versions must increase and retain their resource name.");
        }
    }
    private static void CheckAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Workspace cannot pass through a link or junction.");
    }
}
