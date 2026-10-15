using System.Text.Json;
using System.Text.Json.Serialization;
using StWorkbench.Core;

namespace StWorkbench.Mcp;

public sealed record WorkspaceView(string SessionId, string ProjectPath, string Revision, bool HasUnsavedChanges,
    StProject Project, SourceDocument[] Sources, string Notice = "MCP edits a saved-project draft. Save then reload in the desktop; resolve unsaved desktop edits first.");
public sealed record RenamePreview(string Revision, string OldName, string NewName, SourceDocument[] ChangedSources, string Notice);
public static class McpJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, MaxDepth = 48, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() }
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
