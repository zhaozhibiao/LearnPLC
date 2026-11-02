using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using StWorkbench.Core;

namespace StWorkbench.Toolchain;

public sealed record HardwarePackageResult(bool Success, string PackagePath, string BuildHash, string HardwareHash, IReadOnlyList<string> Errors);

/// <summary>Builds physical configuration only; it never invokes or validates the ST compiler.</summary>
public sealed class HardwarePackageBuilder
{
    public async Task<HardwarePackageResult> BuildAsync(StProject project, string buildRoot, IReadOnlyList<SourceDocument>? sources = null, CancellationToken token = default)
    {
        // Freeze before the first await. Program text and program task validity do not gate hardware repair.
        project = JsonSerializer.Deserialize<StProject>(JsonSerializer.Serialize(project)) ?? throw new InvalidDataException("Empty project.");
        var document = new HardwareConfigurationDocument(project.Hardware) { Network = project.Network, MotionCycle = project.MotionCycle is { Enabled: true } cycle ? cycle : null, Communication = project.Communication };
        var configuration = JsonSerializer.Serialize(document);
        var hardwareHash = ProjectValidator.Hash(configuration);
        var hardwareProject = new StProject { Hardware = project.Hardware, Network = project.Network, MotionCycle = project.MotionCycle };
        var dummy = new ProjectItem(); hardwareProject.Items = [dummy]; hardwareProject.Tasks = [new() { ProgramId = dummy.Id }];
        var errors = ProjectValidator.Validate(hardwareProject, [new(dummy.Id, dummy.SourcePath, "")]).Select(e => e.Message).ToList();
        var runtime = EtherCatRuntimeCompiler.Compile(hardwareProject);
        errors.AddRange(runtime.Blockers);
        if (!runtime.ReadyForRuntime || errors.Count > 0) return new(false, "", "", hardwareHash, errors);
        token.ThrowIfCancellationRequested();
        var root = Path.Combine(Path.GetFullPath(buildRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var period = project.Tasks.Where(t => t.Enabled).Select(t => t.IntervalMilliseconds).ToArray();
        var interval = period.Length == 1 && period[0] is >= 1 and <= 1000 ? period[0] : 10;
        var axisV2 = sources is not null && sources.Count > 0 && AxisControlBinding.UsesDynamicProfile(sources);
        var axisCapabilities = axisV2 ? (AxisControlBinding.UsesDynamicVelocityProfile(sources!) ? new[] { "workbench_axis_move_profile_v2", "workbench_axis_velocity_profile_v2" } : new[] { "workbench_axis_move_profile_v2" }) : Array.Empty<string>();
        var axisInterface = axisV2 ? "workbench-axis-v2" : "workbench-axis-v1";
        var axisManifest = JsonSerializer.Serialize(new { SchemaVersion = 1, InterfaceVersion = axisInterface, PlcIntervalMilliseconds = interval,
            Axes = project.Hardware.Axes.OrderBy(a => a.Name, StringComparer.Ordinal).Select((a, i) => new { Index = i + 1, a.Name, a.ControllerAxis, a.Unit, a.MinPosition, a.MaxPosition,
                a.MaximumVelocity, a.MaximumAcceleration, a.MaximumDeceleration, a.MaximumJerk }) });
        if (axisV2)
        {
            var axisNode = System.Text.Json.Nodes.JsonNode.Parse(axisManifest)!;
            axisNode["CommandBytes"] = 4112;
            axisNode["FeedbackBytes"] = 6656;
            axisNode["RequiredRuntimeCapabilities"] = JsonSerializer.SerializeToNode(axisCapabilities);
            axisManifest = axisNode.ToJsonString();
        }
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["hardware-configuration.json"] = configuration,
            ["ethercat-runtime.json"] = runtime.TopologyJson,
            ["machine-config.json"] = runtime.MachineJson,
            ["io-map.json"] = runtime.IoMapJson,
            ["axis-manifest.json"] = axisManifest
        };
        var inventory = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, content) in files)
        {
            await File.WriteAllTextAsync(Path.Combine(root, name), content, token);
            inventory[name] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, name), token)));
        }
        using var machine = JsonDocument.Parse(runtime.MachineJson);
        var capabilities = machine.RootElement.TryGetProperty("requiredRuntimeCapabilities", out var requirements)
            ? requirements.EnumerateArray().Select(value => value.GetString() ?? throw new InvalidDataException("Invalid required capability.")).ToList()
            : new List<string>();
        if (axisV2)
        {
            foreach (var c in axisCapabilities) if (!capabilities.Contains(c)) capabilities.Add(c);
        }
        // Older files-only identities remain valid for zero/one capability. A corrected multi-capability
        // manifest must use a new recipe identity so an already-staged archive cannot collide with it.
        var recipe = (capabilities.Count > 1 || axisV2) ? "hardware-capabilities-v2" : null;
        var identity = recipe is null ? JsonSerializer.Serialize(new { ArtifactKind = "hardware-only", Files = inventory })
            : JsonSerializer.Serialize(new { ArtifactKind = "hardware-only", BuildRecipeVersion = recipe, Files = inventory });
        var buildHash = ProjectValidator.Hash(identity);
        var manifestNode = new System.Text.Json.Nodes.JsonObject
        {
            ["SchemaVersion"] = 1,
            ["ArtifactKind"] = "hardware-only",
            ["BuildHash"] = buildHash,
            ["RequiredRuntimeCapabilities"] = JsonSerializer.SerializeToNode(capabilities),
            ["HardwareHash"] = hardwareHash,
            ["EtherCatRuntimeInterfaceVersion"] = "workbench-ethercat-v1",
            ["EtherCatTopologyFingerprint"] = runtime.TopologyFingerprint,
            ["Files"] = JsonSerializer.SerializeToNode(inventory)
        };
        if (recipe is not null)
        {
            manifestNode["BuildRecipeVersion"] = recipe;
        }
        if (axisV2)
        {
            manifestNode["AxisInterfaceVersion"] = "workbench-axis-v2";
            manifestNode["AxisExportMarker"] = "workbench_axis_v2";
            manifestNode["AxisCommandBytes"] = 4112;
            manifestNode["AxisFeedbackBytes"] = 6656;
        }
        var manifestJson = manifestNode.ToJsonString();
        await File.WriteAllTextAsync(Path.Combine(root, "build-manifest.json"), manifestJson, token);
        var path = root + ".zip";
        ZipFile.CreateFromDirectory(root, path, CompressionLevel.Optimal, false);
        return new(true, path, buildHash, hardwareHash, []);
    }
}
