using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Schema;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using StWorkbench.Core;

namespace StWorkbench.Mcp;

[McpServerToolType]
public sealed class McpTools(WorkspaceService workspace, ConfigurationService configuration, BuildService builds,
    DeviceService devices, MotionService motion, OperationJobs jobs, ServerOptions options)
{
    [McpServerTool(Name = "workbench_info", ReadOnly = true), Description("Start here. Lists permissions, device bindings, capability limits and the safe engineering workflow. Does not contact devices.")]
    public Task<CallToolResult> Info() => Result(() => Task.FromResult<object>(new
    {
        name = "ST Workbench MCP", version = "1.0.0", transport = "stdio", workspace = workspace.Root,
        workflow = "project_open/create → project_read → edit with expectedRevision → project_validate → project_save → build_start → build_status → device_prepare → device_execute → operation_status",
        motionWorkflow = "motion_capabilities/status → motion_manifest → install approved manifest separately → motion_acquire → motion_prepare → motion_execute → motion_operation → motion_stop/release",
        devices = options.Devices.Select(d => new { d.Id, d.Host, d.Port, d.User, d.AllowPlcControl, d.AllowMotion, d.Backend, d.AllowedAxes }),
        limits = new { drafts = 16, sourcesPerProject = 256, sourceBytes = 1048576, draftBytes = 12582912, buildJobs = 32, jobs = 64, planSeconds = 20, controlSeconds = 30 },
        notes = new[] { "MCP edits saved-project drafts; save and reload desktop after resolving unsaved desktop edits.", "Accepted never means motion completed; Unknown must not be replayed.", "Mechanical/rotary apply, physical timed Jog and continuous machining are capability-gated and currently unsupported.", "MCP is supervisory; Linux core retains realtime control. Stop is ordinary controlled stop, not safety-rated emergency stop." }
    }));

    [McpServerTool(Name = "project_create"), Description("Create an unsaved project draft at a workspace-relative .stproj path. Does not overwrite existing projects. Save explicitly.")]
    public Task<CallToolResult> Create(string name, string projectPath) => Result(() => workspace.CreateAsync(name, projectPath));

    [McpServerTool(Name = "project_open", ReadOnly = true), Description("Open a saved workspace-relative .stproj into an isolated draft. Returns sessionId, revision, project and sources.")]
    public Task<CallToolResult> Open(string projectPath) => Result(() => workspace.OpenAsync(projectPath));

    [McpServerTool(Name = "project_read", ReadOnly = true), Description("Read current draft, source texts, configuration and revision. expectedRevision for all edits must match this revision.")]
    public Task<CallToolResult> Read(string sessionId) => Result(() => workspace.ReadAsync(sessionId));

    [McpServerTool(Name = "project_close", Destructive = true), Description("Discard this MCP draft and free session capacity. Saved files are retained. Unsaved draft edits are lost.")]
    public Task<CallToolResult> Close(string sessionId) => Done(() => workspace.CloseAsync(sessionId));

    [McpServerTool(Name = "source_write"), Description("Replace one source document in a draft using itemId and expectedRevision. Incomplete ST may remain in the draft; validation/build reports errors. Does not save or download.")]
    public Task<CallToolResult> Write(string sessionId, string expectedRevision, Guid itemId, string text) => Result(() => workspace.WriteSourceAsync(sessionId, expectedRevision, itemId, text));

    [McpServerTool(Name = "project_replace", Destructive = true), Description("Atomically replace draft project metadata and full source list. Useful for coordinated task/global/type changes. Preserve project ID and existing immutable path versions. Get schemas with workbench_schema. Does not save.")]
    public Task<CallToolResult> Replace(string sessionId, string expectedRevision, JsonElement project, SourceDocument[] sources) => Result(() => workspace.ReplaceAsync(sessionId, expectedRevision, Parse<StProject>(project), sources));

    [McpServerTool(Name = "project_save", Destructive = true), Description("Validate and atomically save draft via ProjectStore. Refuses external manifest changes. Save does not download; original desktop needs reload and unsaved-edit resolution.")]
    public Task<CallToolResult> Save(string sessionId, string expectedRevision) => Result(() => workspace.SaveAsync(sessionId, expectedRevision));

    [McpServerTool(Name = "project_validate", ReadOnly = true), Description("Run engineering/static binding validation. ST syntax/type correctness additionally requires build_start with real compiler.")]
    public Task<CallToolResult> Validate(string sessionId) => Result(async () =>
    {
        var draft = await workspace.ReadAsync(sessionId); var diagnostics = ProjectValidator.Validate(draft.Project, draft.Sources);
        return new { draft.Revision, success = diagnostics.Count == 0, diagnostics, stage = "engineering-check", compilerCheckRequired = true };
    });

    [McpServerTool(Name = "resource_add"), Description("Add a Program, Function, FunctionBlock, DataType or GlobalVariables resource with a valid ST identifier. Optional sourceText overrides standard template.")]
    public Task<CallToolResult> Add(string sessionId, string expectedRevision, string name, ItemKind kind, string? sourceText = null) => Result(() => workspace.AddResourceAsync(sessionId, expectedRevision, name, kind, sourceText));

    [McpServerTool(Name = "resource_delete", Destructive = true), Description("Remove an unbound resource after conservative reference checks. Task-bound programs and global variable resources require an atomic reviewed project_replace.")]
    public Task<CallToolResult> Delete(string sessionId, string expectedRevision, Guid itemId) => Result(() => workspace.DeleteResourceAsync(sessionId, expectedRevision, itemId));

    [McpServerTool(Name = "resource_rename_preview", ReadOnly = true), Description("Preview token-aware resource rename and changed source documents. Comments/strings remain unchanged. Ambiguous declarations are refused.")]
    public Task<CallToolResult> RenamePreview(string sessionId, string expectedRevision, Guid itemId, string newName) => Result(() => workspace.PreviewRenameAsync(sessionId, expectedRevision, itemId, newName));

    [McpServerTool(Name = "resource_rename", Destructive = true), Description("Apply reviewed rename to the same draft revision. Updates ST definitions/references; preserves comments/strings and existing source paths. GVL label rename does not rename members.")]
    public Task<CallToolResult> Rename(string sessionId, string expectedRevision, Guid itemId, string newName) => Result(() => workspace.RenameResourceAsync(sessionId, expectedRevision, itemId, newName));

    [McpServerTool(Name = "st_symbols", ReadOnly = true), Description("Get ST symbols, completion candidates, and optional function/block signature. Engineering symbol index, not proof of successful compilation.")]
    public Task<CallToolResult> Symbols(string sessionId, string prefix = "", string? signatureName = null) => Result(async () =>
    {
        var view = await workspace.ReadAsync(sessionId); var language = new StLanguageService(); var symbols = BuiltinLibraryCatalog.PublicSymbols(options.RepositoryRoot, view.Sources, view.Project.Communication);
        return new { view.Revision, symbols, completion = language.Complete(symbols, prefix), signature = signatureName is null ? null : language.Signature(symbols, signatureName) };
    });

    [McpServerTool(Name = "st_instructions", ReadOnly = true), Description("List original standard ST examples (logic, IF/ELSE, timers, counters) and available axis/path function block names. search filters standard and configured engineering-axis templates.")]
    public Task<CallToolResult> Instructions(string search = "", string? sessionId = null) => Result(async () =>
    {
        var library = new List<SourceDocument>();
        foreach (var (folder, file) in new[] { ("axis-bridge-v2", "axis_control.st"), ("motion-bridge", "continuous_path.st") })
            library.Add(new(Guid.NewGuid(), "library/" + file, await File.ReadAllTextAsync(Path.Combine(options.RepositoryRoot, "tools", folder, file))));
        var signatures = new StLanguageService().Index(library).Where(s => s.Name.StartsWith("MC_", StringComparison.Ordinal) || s.Owner?.StartsWith("MC_", StringComparison.Ordinal) == true || s.Name == "AXIS_REF").ToArray();
        var draft = sessionId is null ? null : await workspace.ReadAsync(sessionId);
        var axisTemplates = AxisInstructionCatalog.Create(draft?.Project.Hardware.Axes ?? [], includeCompatibilityAliases: true).ToArray();
        var moveTemplate = axisTemplates.FirstOrDefault(i => i.Id.EndsWith("_MC_MoveAbsolute", StringComparison.Ordinal));
        const string emptyProgram = "PROGRAM Main\nVAR\nEND_VAR\n\nEND_PROGRAM\n";
        return new
        {
            templates = StInstructionCatalog.All.Concat(axisTemplates).Where(i => (i.Id + i.Name + i.Category + i.Description).Contains(search, StringComparison.OrdinalIgnoreCase)),
            axisBlocks = AxisControlBinding.BlockNames, motionSignatures = signatures,
            axisConstants = draft is null ? null : AxisControlBinding.Constants(draft.Project.Hardware.Axes),
            paths = draft?.Project.Paths.Select(p => new { p.Name, p.Id, p.NumericId, p.Version, p.ContentHash, p.Axes }),
            axisExample = moveTemplate is null ? null : StSnippetInsertion.Insert(emptyProgram, emptyProgram.IndexOf("END_PROGRAM", StringComparison.Ordinal), moveTemplate).Text,
            note = "Declare an actual configured engineering axis; do not use an unknown AXIS_X. MC_ interfaces are supplied by package generation; do not copy private AB_/MB_ globals into user ST. Runtime readiness is separate."
        };
    });

    [McpServerTool(Name = "st_insert_instruction"), Description("Insert a catalog template at a UTF-16 code offset, including required declarations and unique instance names. Uses the same insertion engine as desktop dragging.")]
    public Task<CallToolResult> Insert(string sessionId, string expectedRevision, Guid itemId, int offset, string instructionId) => Result(() =>
        workspace.EditAsync(sessionId, expectedRevision, (project, sources) =>
        {
            var at = sources.FindIndex(s => s.ItemId == itemId); if (at < 0) throw new ArgumentException("Unknown source.");
            var instruction = StInstructionCatalog.All
                .Concat(AxisInstructionCatalog.Create(project.Hardware.Axes, includeCompatibilityAliases: true))
                .Concat(BuiltinLibraryCatalog.ControlInstructions(options.RepositoryRoot, project.Tasks.FirstOrDefault(t => t.Enabled)?.IntervalMilliseconds / 1000.0 ?? 0.01))
                .Concat(BuiltinLibraryCatalog.CommunicationInstructions(project.Communication))
                .SingleOrDefault(i => i.Id == instructionId) ?? throw new ArgumentException("Unknown instruction ID.");
            sources[at] = sources[at] with { Text = StSnippetInsertion.Insert(sources[at].Text, offset, instruction).Text };
        }));

    [McpServerTool(Name = "configuration_update", Destructive = true), Description("Atomically replace offline hardware, network, tasks and motion cycle configuration using current revision. Task Scheduling is Ordinary (default SCHED_OTHER) or Realtime (SCHED_FIFO below motion), Priority is IEC 0 highest to 31 lowest, Cpu is optional. Enabled tasks must bind different programs. Null network/motionCycle clears those sections. Does not apply to physical device.")]
    public Task<CallToolResult> Configure(string sessionId, string expectedRevision, HardwareConfiguration hardware, PlcTask[] tasks, NetworkConfiguration? network = null, MotionCycleConfiguration? motionCycle = null) =>
        Result(() => configuration.ConfigureAsync(sessionId, expectedRevision, hardware, network, tasks, motionCycle));

    [McpServerTool(Name = "esi_import"), Description("Read a bounded ESI XML from inside workspace and add device definitions to draft. Uses safe XML parser. Configure stations using configuration_update.")]
    public Task<CallToolResult> Esi(string sessionId, string expectedRevision, string xmlPath) => Result(() => configuration.ImportEsiAsync(sessionId, expectedRevision, xmlPath));

    [McpServerTool(Name = "io_auto_map"), Description("Allocate IO addresses for an existing engineering station, preserving existing IO addresses. Returns additions and unsupported PDO reasons. Draft only.")]
    public Task<CallToolResult> AutoMap(string sessionId, string expectedRevision, Guid stationId) => Result(() => configuration.AutoMapAsync(sessionId, expectedRevision, stationId));

    [McpServerTool(Name = "communication_configure"), Description("Set offline Linux Modbus-master/serial-point-to-point/TCP-client channels. Server/slave roles are unavailable. Uses draft revision; no port is opened and no device data is written.")]
    public Task<CallToolResult> ConfigureCommunication(string sessionId, string expectedRevision, CommunicationConfiguration? communication = null) => Result(() => workspace.EditAsync(sessionId, expectedRevision, (project, _) => project.Communication = McpJson.Clone(communication)));

    [McpServerTool(Name = "control_communication_catalog", ReadOnly = true), Description("Get exact original ST control/communication library signatures and executable insertion templates, bound communication constants and offline settings. No physical IO.")]
    public Task<CallToolResult> LibraryCatalog(string sessionId) => Result(async () =>
    {
        var draft = await workspace.ReadAsync(sessionId);
        var paths = new[] { "tools/control-library/control.st", "tools/communication-bridge/communication.st" };
        var sources = new List<SourceDocument>(); foreach (var path in paths) sources.Add(new(Guid.NewGuid(), path, await File.ReadAllTextAsync(Path.Combine(options.RepositoryRoot, path))));
        var symbols = new StLanguageService().Index(sources).Where(s => s.Owner?.StartsWith("CB_", StringComparison.Ordinal) != true && !s.Name.StartsWith("CB_", StringComparison.Ordinal)).ToArray();
        return new { draft.Revision, draft.Project.Communication, symbols, templates = BuiltinLibraryCatalog.ControlInstructions(options.RepositoryRoot, draft.Project.Tasks.First(t => t.Enabled).IntervalMilliseconds / 1000.0).Concat(BuiltinLibraryCatalog.CommunicationInstructions(draft.Project.Communication)).ToArray(), constants = draft.Project.Communication is { } c ? CommunicationBinding.Constants(c) : null };
    });

    [McpServerTool(Name = "dxf_preview", ReadOnly = true), Description("Parse workspace DXF using millimetersPerUnit scale. Returns already-scaled contours; selecting another scale requires reparsing original DXF. No stored path changes.")]
    public Task<CallToolResult> Dxf(string sessionId, string expectedRevision, string dxfPath, double millimetersPerUnit = 1) => Result(() => configuration.ImportDxfAsync(sessionId, expectedRevision, dxfPath, millimetersPerUnit));

    [McpServerTool(Name = "path_store"), Description("Validate/plan and append an immutable path version to draft. Use scaled preview segments, valid ST name and configured linear-mm XY/XYZ axes. Computes content hash.")]
    public Task<CallToolResult> StorePath(string sessionId, string expectedRevision, JsonElement path) => Result(() => configuration.StorePathAsync(sessionId, expectedRevision, Parse<MotionPathAsset>(path)));

    [McpServerTool(Name = "path_plan", ReadOnly = true), Description("Compute bounded offline trajectory duration for stored path ID/version. Excludes positioning to start and device waiting. Does not execute motion.")]
    public Task<CallToolResult> PathPlan(string sessionId, Guid pathId, uint version) => Result(() => configuration.PlanPathAsync(sessionId, pathId, version));

    [McpServerTool(Name = "build_start"), Description("Check and compile frozen draft using configured independent compiler; generate actual candidate package. Returns operation ID immediately. Poll build_status for diagnostics and build hash. Never downloads.")]
    public Task<CallToolResult> Build(string sessionId, string expectedRevision) => Result(() => builds.StartAsync(sessionId, expectedRevision));

    [McpServerTool(Name = "build_status", ReadOnly = true), Description("Read compiler job status, bounded log, located diagnostics and verified build identity.")]
    public Task<CallToolResult> BuildStatus(string operationId) => Result(() => Task.FromResult(builds.Read(operationId)));

    [McpServerTool(Name = "build_cancel"), Description("Cancel a compiler job; no device operation is performed.")]
    public Task<CallToolResult> CancelBuild(string operationId) => Result(() => { builds.Cancel(operationId); return Task.FromResult(new { cancelled = operationId }); });

    [McpServerTool(Name = "device_status", ReadOnly = true, OpenWorld = true), Description("Query configured Linux coordinator status and bounded run/build logs over strict SSH. Device ID must be configured by operator. Does not start or stop PLC.")]
    public Task<CallToolResult> DeviceStatus(string deviceId) => Result(() => devices.StatusAsync(deviceId));

    [McpServerTool(Name = "device_prepare", ReadOnly = true, OpenWorld = true), Description("Prepare short-lived download/start/stop plan for saved project. Returns restart impact, original run/stop state and active/candidate hashes. download requires successful matching buildOperationId. Does not change PLC state.")]
    public Task<CallToolResult> DevicePrepare(string deviceId, string action, string sessionId, string expectedRevision, string? buildOperationId = null) => Result(() => devices.PrepareAsync(deviceId, action, sessionId, expectedRevision, buildOperationId));

    [McpServerTool(Name = "device_execute", Destructive = true, OpenWorld = true), Description("Consume reviewed single-use device plan. Can download, compile, restart core, apply program or change PLC run state. Download restores original run/stop state. Poll operation_status. Requires startup AllowPlcControl.")]
    public Task<CallToolResult> DeviceExecute(string planId) => Result(() => Task.FromResult(devices.Execute(planId)));

    [McpServerTool(Name = "operation_status", ReadOnly = true), Description("Read background PLC operation outcome. Unknown means actual device state needs refresh; never retry automatically.")]
    public Task<CallToolResult> OperationStatus(string operationId) => Result(() => Task.FromResult(jobs.Read(operationId)));

    [McpServerTool(Name = "motion_capabilities", ReadOnly = true, OpenWorld = true), Description("Open/query configured motion gateway readonly. Returns real/simulator backend, matching manifest identity and supported capabilities. Does not acquire axis control.")]
    public Task<CallToolResult> MotionCapabilities(string deviceId) => Result(() => motion.CapabilitiesAsync(deviceId));

    [McpServerTool(Name = "motion_status", ReadOnly = true, OpenWorld = true), Description("Read actual axis state, positions, velocities, units and sample cycle. No ownership acquisition or synthetic values.")]
    public Task<CallToolResult> MotionStatus(string deviceId) => Result(() => motion.StatusAsync(deviceId));

    [McpServerTool(Name = "motion_manifest", ReadOnly = true), Description("Return exact basic axis manifest JSON/hash expected for this saved project. Operator must install it beside configured gateway; this tool does not change remote config or apply axis mechanics.")]
    public Task<CallToolResult> Manifest(string sessionId) => Result(async () =>
    {
        var draft = await workspace.ReadAsync(sessionId); WorkspaceService.RequireValid(draft.Project, draft.Sources);
        if (draft.HasUnsavedChanges || draft.Project.Hardware.Axes.Count is < 1 or > 64 || draft.Project.Hardware.Axes.Any(a => a.Mechanics is not null)) throw new InvalidOperationException("Save a supported basic-axis project first; explicit mechanics apply is unavailable.");
        return new { draft.Revision, manifest = MotionService.ManifestJson(draft.Project), sha256 = MotionService.ManifestHash(draft.Project) };
    });

    [McpServerTool(Name = "motion_acquire", Destructive = true, OpenWorld = true), Description("Acquire exclusive axis controller lease for a saved matching project; refuses PLC/other owner. Control session max30s; lease heartbeat handled in Linux. durationMilliseconds250..30000 controls device lease watchdog; simulator Jog uses same duration. No physical movement here.")]
    public Task<CallToolResult> Acquire(string deviceId, string sessionId, string expectedRevision, int durationMilliseconds = 1000) => Result(() => motion.AcquireAsync(deviceId, sessionId, expectedRevision, durationMilliseconds));

    [McpServerTool(Name = "motion_prepare", ReadOnly = true, OpenWorld = true), Description("Prepare 20s single-use axis action plan: Enable/Disable/Reset/Home/MoveAbsolute/Jog. Binds device, saved project, axis map and units. Real Jog/dynamic-parameter application currently capability-gated; simulator Jog has maximum duration only. No movement.")]
    public Task<CallToolResult> MotionPrepare(string deviceId, string sessionId, string expectedRevision, string axis, string kind, double position = 0, double velocity = 0, int durationMilliseconds = 1000) =>
        Result(() => motion.PrepareAsync(deviceId, sessionId, expectedRevision, axis, kind, position, velocity, durationMilliseconds));

    [McpServerTool(Name = "motion_execute", Destructive = true, OpenWorld = true), Description("Consume reviewed motion plan and send actual axis command once. Requires configured AllowMotion and prior ownership. Can physically enable/home/move an axis. Accepted is not Completed; poll motion_operation. Never auto-replay unknown results.")]
    public Task<CallToolResult> MotionExecute(string planId) => Result(() => motion.ExecuteAsync(planId));

    [McpServerTool(Name = "motion_stop", Destructive = true, OpenWorld = true), Description("Immediately request ordinary controlled Stop for allowlisted owned axis, preempting this session's busy action. No plan required. This is not hardware emergency stop. Returns actual operation ID to query.")]
    public Task<CallToolResult> Stop(string deviceId, string axis) => Result(() => motion.StopAsync(deviceId, axis));

    [McpServerTool(Name = "motion_operation", ReadOnly = true, OpenWorld = true), Description("Read exact axis operation result/feedback. Completed requires device proof. Unknown/Aborted is never reported as successful motion.")]
    public Task<CallToolResult> MotionOperation(string deviceId, string operationId) => Result(() => motion.ReadOperationAsync(deviceId, operationId));

    [McpServerTool(Name = "motion_release", Destructive = true, OpenWorld = true), Description("Stop owned axes and release this session's control. Existing controller applies safe state and may disable axes. Does not steal another owner's control.")]
    public Task<CallToolResult> Release(string deviceId) => Result(() => motion.ReleaseAsync(deviceId));

    [McpServerTool(Name = "motion_close", Destructive = true, OpenWorld = true), Description("Close motion gateway connection; best-effort stop/release owned axes and invalidate plans. Required explicit session reset after disconnect; never automatic motion replay.")]
    public Task<CallToolResult> MotionClose(string deviceId) => Done(() => motion.CloseAsync(deviceId));

    [McpServerTool(Name = "path_execute", Destructive = true, OpenWorld = true), Description("Report current continuous path execution gate. Existing real backend does not support confirmed continuous interpolation; this tool refuses instead of substituting point-stop motion.")]
    public Task<CallToolResult> PathExecute(string deviceId, Guid pathId, uint version) => Result<object>(() => throw new InvalidOperationException("capability_missing: physical continuous trajectory execution is not implemented by the current backend. Path " + pathId + " v" + version + " was not sent to " + deviceId + "."));

    [McpServerTool(Name = "workbench_schema", ReadOnly = true), Description("Get JSON schemas used for project_replace, configuration_update and path_store. Core property names are PascalCase; enum values are strings.")]
    public Task<CallToolResult> Schema() => Result(() => Task.FromResult<object>(new
    {
        project = EngineeringSchemas.For(typeof(StProject)), sources = EngineeringSchemas.For(typeof(SourceDocument[])),
        hardware = EngineeringSchemas.For(typeof(HardwareConfiguration)), tasks = EngineeringSchemas.For(typeof(PlcTask[])), path = EngineeringSchemas.For(typeof(MotionPathAsset))
    }));

    private static async Task<CallToolResult> Done(Func<Task> action) => await Result(async () => { await action(); return new { success = true }; });
    private static T Parse<T>(JsonElement element) => element.Deserialize<T>(McpJson.Options) ?? throw new InvalidDataException("Empty structured tool argument.");
    private static async Task<CallToolResult> Result<T>(Func<Task<T>> action)
    {
        try
        {
            var value = await action(); var json = JsonSerializer.Serialize(value, McpJson.Options);
            var isError = value is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False;
            return new CallToolResult { IsError = isError, StructuredContent = JsonSerializer.SerializeToElement(value, McpJson.Options), Content = [new TextContentBlock { Text = json }] };
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            var message = e.Message.Length > 4096 ? e.Message[..4096] : e.Message;
            var error = new { success = false, error = e.GetType().Name, message };
            return new CallToolResult { IsError = true, StructuredContent = JsonSerializer.SerializeToElement(error, McpJson.Options), Content = [new TextContentBlock { Text = JsonSerializer.Serialize(error, McpJson.Options) }] };
        }
    }
}
