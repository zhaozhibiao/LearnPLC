using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StWorkbench.Core;
using StWorkbench.Motion;

namespace StWorkbench.Toolchain;

public sealed record PackageResult(BuildResult Compilation, string PackagePath, string BuildHash, string SymbolTableHash)
{
    public bool Success => Compilation.Success && !string.IsNullOrEmpty(PackagePath);
    public bool SafeForDirectUpload => false;
    public bool RuntimeLinkVerified => false;
    public bool CommercialRedistributionCleared => false;
}

public sealed class PackageBuilder
{
    public const string RuntimeCommit = "a9e38d901fb87deb5a8cf778b52639b9387834d0";
    private readonly string runtimeCompatibilityDirectory;
    public PackageBuilder(string? runtimeCompatibilityDirectory = null)
    {
        this.runtimeCompatibilityDirectory = Path.GetFullPath(runtimeCompatibilityDirectory ?? Path.Combine("tools", "runtime-compat"));
    }
    public async Task<PackageResult> BuildAsync(BuildSnapshot snapshot, IReadOnlyDictionary<Guid, string> programNames,
        string nodeExecutable, string adapterPath, string compilerDistributionDirectory, string buildRoot, CancellationToken token = default)
    {
        var sources = snapshot.Sources.ToArray();
        var pathAssets = snapshot.Paths.Select(p => p with { Axes = p.Axes.ToArray(), Segments = p.Segments.ToArray(), Profile = p.Profile with { } }).ToArray();
        var tasks = snapshot.Tasks.ToArray();
        var axisUsed = AxisControlBinding.UsesAxisControl(sources);
        var controlUsed = ControlLibraryBinding.IsUsed(sources);
        var communicationUsed = CommunicationBinding.IsUsed(sources);
        var names = programNames.OrderBy(pair => pair.Key).ToDictionary(pair => pair.Key, pair => pair.Value);
        var cycle = snapshot.MotionCycle is { Enabled: true } enabledCycle ? enabledCycle with { AxisNames = enabledCycle.AxisNames?.ToArray() ?? [] } : null;
        var communication = snapshot.Communication is { } comm ? comm with { Channels = comm.Channels.Select(c => c with { }).ToArray() } : null;
        var frozen = snapshot with { Sources = sources, Tasks = tasks, Paths = pathAssets, Axes = snapshot.Axes.ToArray(), MotionCycle = cycle, Communication = communication };
        BuildResult? compilation = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (frozen.HardwareConfigurationJson is { } hardwareJson)
            {
                if (ProjectValidator.Hash(hardwareJson) != frozen.HardwareHash) throw new InvalidDataException("Hardware configuration differs from captured identity.");
                var configuration = JsonSerializer.Deserialize<HardwareConfigurationDocument>(hardwareJson) ?? throw new InvalidDataException("Hardware configuration is empty.");
                if (configuration.SchemaVersion != 1 || configuration.State != "offline-engineering" ||
                    configuration.Network is null && configuration.Hardware?.Stations is null && configuration.Hardware?.Axes.Any(a => a.Mechanics is not null) != true && configuration.Communication is null)
                    throw new InvalidDataException("Unsupported hardware configuration document.");
                var item = new ProjectItem();
                var configuredProject = new StProject { Items = [item], Tasks = [new PlcTask { ProgramId = item.Id }], Hardware = configuration.Hardware!, Network = configuration.Network, MotionCycle = configuration.MotionCycle, Communication = configuration.Communication };
                var configurationErrors = ProjectValidator.Validate(configuredProject, [new(item.Id, item.SourcePath, "")]);
                if (configurationErrors.Count > 0) throw new InvalidDataException(string.Join("\n", configurationErrors.Select(e => e.Message)));
                if (JsonSerializer.Serialize(configuration.Hardware!.Axes) != JsonSerializer.Serialize(frozen.Axes) || JsonSerializer.Serialize(configuration.MotionCycle) != JsonSerializer.Serialize(cycle))
                    throw new InvalidDataException("Hardware configuration axis/cycle metadata differs from captured snapshot.");
                if (JsonSerializer.Serialize(configuration.Communication) != JsonSerializer.Serialize(communication)) throw new InvalidDataException("Communication metadata differs from captured snapshot.");
            }
            var cycleErrors = MotionCycleValidator.Validate(cycle, frozen.Axes, pathAssets);
            if (cycleErrors.Count > 0) throw new InvalidDataException(string.Join("\n", cycleErrors));
            var bindingErrors = PathBindingValidator.Validate(sources, pathAssets);
            if (bindingErrors.Count > 0) throw new InvalidDataException(string.Join("\n", bindingErrors.Select(e => e.Message)));
            var axisErrors = AxisControlBinding.Validate(sources, frozen.Axes, tasks);
            if (axisErrors.Count > 0) throw new InvalidDataException(string.Join("\n", axisErrors.Select(e => e.Message)));
            var libraryErrors = ControlLibraryBinding.Validate(sources).Concat(CommunicationBinding.Validate(communication, sources, tasks.Select(t => new PlcTask { Name = t.Name, ProgramId = t.ProgramId, IntervalMilliseconds = t.IntervalMilliseconds, Enabled = t.Enabled }).ToArray())).ToArray();
            if (libraryErrors.Length > 0) throw new InvalidDataException(string.Join("\n", libraryErrors.Select(e => e.Message)));
            if (axisUsed)
            {
                var mappingErrors = MotionCycleValidator.Validate(new() { Enabled = true, AxisNames = frozen.Axes.Select(a => a.Name).ToArray() }, frozen.Axes, []);
                if (mappingErrors.Count > 0) throw new InvalidDataException(string.Join("\n", mappingErrors));
            }
            if (sources.Length == 0 || tasks.Count(task => task.Enabled) != 1) throw new InvalidOperationException("Exactly one enabled task and ST sources are required.");
            var activeTask = tasks.Single(task => task.Enabled);
            if (activeTask.IntervalMilliseconds <= 0 || !sources.Any(source => source.ItemId == activeTask.ProgramId) || !names.ContainsKey(activeTask.ProgramId))
                throw new InvalidOperationException("Task interval or source/program mapping is invalid.");
            var packageRoot = Path.Combine(Path.GetFullPath(buildRoot), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(packageRoot);
            var effectiveAdapter = axisUsed ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(adapterPath))!, "compile-axis.mjs") : pathAssets.Length == 0 ? adapterPath : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(adapterPath))!, "compile-motion.mjs");
            if (controlUsed || communicationUsed) effectiveAdapter = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(adapterPath))!, "compile-extended.mjs");
            var controlDirectory = Path.GetFullPath(Path.Combine(runtimeCompatibilityDirectory, "..", "control-library"));
            var communicationDirectory = Path.GetFullPath(Path.Combine(runtimeCompatibilityDirectory, "..", "communication-bridge"));
            var communicationWorker = Path.GetFullPath(Path.Combine(runtimeCompatibilityDirectory, "..", "..", "linux", "communication"));
            var extensionFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (controlUsed) foreach (var file in new[] { "control.st", "LICENSE" }) extensionFiles.Add("control/" + file, await FileHashAsync(Path.Combine(controlDirectory, file), token));
            if (communicationUsed)
            {
                if (communication is null) throw new InvalidDataException("Communication channel configuration required.");
                var errors = CommunicationBinding.Validate(communication, sources, tasks.Select(t => new PlcTask { Name = t.Name, ProgramId = t.ProgramId, IntervalMilliseconds = t.IntervalMilliseconds, Enabled = t.Enabled }).ToArray());
                if (errors.Count > 0) throw new InvalidDataException(string.Join("; ", errors.Select(e => e.Message)));
                foreach (var file in new[] { "communication.st", "communication_bridge.hpp", "communication_bridge.cpp", "generated_communication_scan_shim.cpp", "communication_lifecycle.inc", "LICENSE" }) extensionFiles.Add(file, await FileHashAsync(Path.Combine(communicationDirectory, file), token));
                foreach (var file in new[] { "communication_worker.py", "LICENSE" }) extensionFiles.Add("communication/" + file, await FileHashAsync(Path.Combine(communicationWorker, file), token));
            }
            var communicationConstants = Path.Combine(packageRoot, "communication-constants.st");
            if (communicationUsed) await File.WriteAllTextAsync(communicationConstants, CommunicationBinding.Constants(communication!), token);
            var axisDirectory = Path.GetFullPath(Path.Combine(runtimeCompatibilityDirectory, "..", "axis-bridge"));
            var axisWorkerDirectory = Path.GetFullPath(Path.Combine(runtimeCompatibilityDirectory, "..", "..", "linux", "axis"));
            var axisFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var constantsPath = Path.Combine(packageRoot, "axis-constants.st");
            if (axisUsed)
            {
                foreach (var file in new[] { "axis_control.st", "axis_bridge.hpp", "axis_bridge.cpp", "generated_axis_scan_shim.cpp", "axis_lifecycle.inc" }) axisFiles.Add(file, await FileHashAsync(Path.Combine(axisDirectory, file), token));
                axisFiles.Add("axis_worker.py", await FileHashAsync(Path.Combine(axisWorkerDirectory, "axis_worker.py"), token));
                await File.WriteAllTextAsync(constantsPath, AxisControlBinding.Constants(frozen.Axes), token);
            }
            var motionDirectory = Path.GetFullPath(Path.Combine(runtimeCompatibilityDirectory, "..", "motion-bridge"));
            var workerDirectory = Path.GetFullPath(Path.Combine(runtimeCompatibilityDirectory, "..", "..", "linux", "motion"));
            var motionFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (pathAssets.Length > 0)
            {
                foreach (var file in new[] { "continuous_path.st", "motion_bridge.hpp", "motion_bridge.cpp", "generated_scan_shim.cpp", "runtime_lifecycle.inc" }) motionFiles.Add(file, await FileHashAsync(Path.Combine(motionDirectory, file), token));
                motionFiles.Add("virtual_worker.py", await FileHashAsync(Path.Combine(workerDirectory, "virtual_worker.py"), token));
                if (!File.Exists(effectiveAdapter)) throw new InvalidOperationException("Motion compiler adapter is missing.");
            }
            var distribution = Path.GetFullPath(compilerDistributionDirectory);
            var runtimeIncludes = Path.Combine(distribution, "runtime", "include");
            if (!Directory.Exists(runtimeIncludes)) throw new InvalidOperationException("Compiler Runtime include directory is missing.");
            var headers = Directory.GetFiles(runtimeIncludes, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var runtimeHeaderHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var unmarkedHeaders = new List<string>();
            var headerSources = new Dictionary<string, string>(StringComparer.Ordinal);
            var replacements = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var header in headers)
            {
                var relativeHeader = Path.GetRelativePath(runtimeIncludes, header).Replace('\\', '/');
                var source = header;
                if (relativeHeader is "iec_global.hpp" or "iec_fault.hpp")
                {
                    source = Path.Combine(runtimeCompatibilityDirectory, "include", relativeHeader);
                    if (!File.Exists(source)) throw new InvalidOperationException("Independent runtime compatibility header is missing: " + relativeHeader);
                    var replacementContent = await File.ReadAllTextAsync(source, token);
                    if (!replacementContent.Contains("SPDX-License-Identifier: MIT", StringComparison.Ordinal))
                        throw new InvalidOperationException("Independent runtime compatibility header lacks its MIT notice: " + relativeHeader);
                    replacements.Add(relativeHeader, await FileHashAsync(source, token));
                }
                else if (!(await File.ReadAllTextAsync(header, token)).Contains("GPL-3.0-or-later WITH STruCpp-runtime-exception", StringComparison.Ordinal))
                    unmarkedHeaders.Add(relativeHeader);
                headerSources.Add(relativeHeader, source);
                runtimeHeaderHashes.Add(relativeHeader, await FileHashAsync(source, token));
            }
            if (headers.Length == 0) throw new InvalidOperationException("Compiler runtime headers are empty.");
            if (replacements.Count != 2 || unmarkedHeaders.Count > 0) throw new InvalidOperationException("Runtime header license coverage is incomplete.");
            var compatibilityLicense = Path.Combine(runtimeCompatibilityDirectory, "LICENSE");
            if (!File.Exists(compatibilityLicense)) throw new InvalidOperationException("Independent runtime compatibility MIT license is missing.");
            var libraryHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var libraryDirectory = Path.Combine(distribution, "libs");
            if (!Directory.Exists(libraryDirectory)) throw new InvalidOperationException("Pinned compiler library directory is missing.");
            foreach (var library in Directory.GetFiles(libraryDirectory, "*.stlib")) libraryHashes.Add(Path.GetFileName(library), await FileHashAsync(library, token));
            var inputIdentity = JsonSerializer.Serialize(new { snapshot.ProjectId, snapshot.SourceHash, snapshot.HardwareHash, Sources = sources,
                Tasks = tasks, ProgramNames = names, CompilerVersion = "0.7.0", RuntimeCommit, RuntimeHeaderHashes = runtimeHeaderHashes,
                CompilerLibraryHashes = libraryHashes, RuntimeCompatibilityLicenseHash = await FileHashAsync(compatibilityLicense, token),
                AdapterHash = await FileHashAsync(effectiveAdapter, token) });
            if (cycle is not null) inputIdentity = JsonSerializer.Serialize(new { Base = inputIdentity, MotionCycle = cycle });
            if (axisUsed) inputIdentity = JsonSerializer.Serialize(new { Base = inputIdentity, AxisInterfaceVersion = "workbench-axis-v1", Files = axisFiles, Axes = frozen.Axes.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray() });
            if (pathAssets.Length > 0) inputIdentity = JsonSerializer.Serialize(new { Base = inputIdentity, MotionInterfaceVersion = "workbench-continuous-v1", Files = motionFiles, Paths = pathAssets });
            if (controlUsed || communicationUsed) inputIdentity = JsonSerializer.Serialize(new { Base = inputIdentity, ExtensionFiles = extensionFiles, Communication = communicationUsed ? communication : null });
            var programMd5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(inputIdentity))).ToLowerInvariant();
            var requestPath = Path.Combine(packageRoot, "request.json");
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { Sources = sources, Tasks = tasks, ProgramNames = names,
                ProgramMd5 = programMd5, CompilerDistributionDirectory = distribution, MotionLibraryPath = pathAssets.Length == 0 ? null : Path.Combine(motionDirectory, "continuous_path.st"), AxisLibraryPath = axisUsed ? Path.Combine(axisDirectory, "axis_control.st") : null, AxisConstantsPath = axisUsed ? constantsPath : null,
                ControlLibraryPath = controlUsed ? Path.Combine(controlDirectory, "control.st") : null, CommunicationLibraryPath = communicationUsed ? Path.Combine(communicationDirectory, "communication.st") : null, CommunicationConstantsPath = communicationUsed ? communicationConstants : null }), token);
            compilation = await new ExternalCompiler(expectedCompilerVersion: "STruC++ version 0.7.0",
                executableArguments: [Path.GetFullPath(effectiveAdapter), "--request", requestPath], outputFileName: "generated.cpp")
                .CompileAsync(frozen, nodeExecutable, packageRoot, token);
            if (!compilation.Success) return new(compilation, "", "", "");
            var output = compilation.OutputDirectory;
            if (controlUsed)
            {
                Directory.CreateDirectory(Path.Combine(output, "control"));
                foreach (var file in new[] { "control.st", "LICENSE" }) File.Copy(Path.Combine(controlDirectory, file), Path.Combine(output, "control", file));
            }
            if (communicationUsed)
            {
                foreach (var file in new[] { "communication.st", "communication_bridge.hpp", "communication_bridge.cpp", "generated_communication_scan_shim.cpp", "communication_lifecycle.inc" }) File.Copy(Path.Combine(communicationDirectory, file), Path.Combine(output, file));
                File.Copy(communicationConstants, Path.Combine(output, "communication-constants.st"));
                await File.WriteAllTextAsync(Path.Combine(output, "communication-manifest.json"), CommunicationBinding.Manifest(communication!, activeTask.IntervalMilliseconds), token);
                Directory.CreateDirectory(Path.Combine(output, "communication"));
                foreach (var file in new[] { "communication_worker.py", "LICENSE" }) File.Copy(Path.Combine(communicationWorker, file), Path.Combine(output, "communication", file));
            }
            if (frozen.HardwareConfigurationJson is { } hardwareConfiguration)
                await File.WriteAllTextAsync(Path.Combine(output, "hardware-configuration.json"), hardwareConfiguration, token);
            if (axisUsed)
            {
                foreach (var file in new[] { "axis_control.st", "axis_bridge.hpp", "axis_bridge.cpp", "generated_axis_scan_shim.cpp", "axis_lifecycle.inc" }) File.Copy(Path.Combine(axisDirectory, file), Path.Combine(output, file));
                File.Copy(constantsPath, Path.Combine(output, "axis-constants.st"));
                var axesDirectory = Path.Combine(output, "axes"); Directory.CreateDirectory(axesDirectory);
                File.Copy(Path.Combine(axisWorkerDirectory, "axis_worker.py"), Path.Combine(axesDirectory, "axis_worker.py"));
                File.Copy(Path.Combine(axisWorkerDirectory, "LICENSE"), Path.Combine(axesDirectory, "LICENSE"));
                await File.WriteAllTextAsync(Path.Combine(output, "axis-manifest.json"), JsonSerializer.Serialize(new { SchemaVersion = 1, InterfaceVersion = "workbench-axis-v1", PlcIntervalMilliseconds = activeTask.IntervalMilliseconds,
                    Axes = frozen.Axes.OrderBy(a => a.Name, StringComparer.Ordinal).Select((a, i) =>
                    {
                        var axis = new Dictionary<string, object?>
                        {
                            ["Index"] = i + 1, ["Name"] = a.Name, ["ControllerAxis"] = a.ControllerAxis,
                            ["Unit"] = a.Unit, ["MinPosition"] = a.MinPosition, ["MaxPosition"] = a.MaxPosition,
                            ["MaximumVelocity"] = a.MaximumVelocity, ["MaximumAcceleration"] = a.MaximumAcceleration,
                            ["MaximumDeceleration"] = a.MaximumDeceleration, ["MaximumJerk"] = a.MaximumJerk
                        };
                        if (a.Mechanics is not null) axis.Add("Mechanics", a.Mechanics);
                        return axis;
                    }) }), token);
            }
            if (cycle is not null)
                await File.WriteAllTextAsync(Path.Combine(output, "motion-cycle.json"), JsonSerializer.Serialize(new
                {
                    SchemaVersion = 1, Executor = "EcMotion", Configuration = cycle,
                    Axes = frozen.Axes.Where(a => cycle.AxisNames.Contains(a.Name, StringComparer.OrdinalIgnoreCase)).ToArray()
                }), token);
            if (pathAssets.Length > 0)
            {
                var pathDirectory = Path.Combine(output, "paths"); Directory.CreateDirectory(pathDirectory);
                var prepared = pathAssets.Select(asset => new { Asset = asset, Trajectory = ContinuousPathPlanner.Prepare(asset, frozen.Axes) }).ToArray();
                var payload = JsonSerializer.Serialize(new { SchemaVersion = 1, Backend = "VirtualContinuous", Paths = prepared });
                if (Encoding.UTF8.GetByteCount(payload) > 16 * 1024 * 1024) throw new InvalidDataException("Prepared trajectory bundle exceeds the 16 MiB archive member limit; split the path.");
                await File.WriteAllTextAsync(Path.Combine(pathDirectory, "motion-manifest.json"), payload, token);
                foreach (var file in new[] { "continuous_path.st", "motion_bridge.hpp", "motion_bridge.cpp", "generated_scan_shim.cpp", "runtime_lifecycle.inc" }) File.Copy(Path.Combine(motionDirectory, file), Path.Combine(output, file));
                File.Copy(Path.Combine(workerDirectory, "virtual_worker.py"), Path.Combine(pathDirectory, "virtual_worker.py"));
                await File.WriteAllTextAsync(Path.Combine(pathDirectory, "LICENSE"), "SPDX-License-Identifier: MIT\nCopyright (c) 2026 ST Workbench contributors\nSee RUNTIME-COMPAT-LICENSE for the complete MIT license.\n", token);
            }
            foreach (var file in new[] { "generated_debug.cpp", "debug-map.json", "configuration.st" })
                if (!File.Exists(Path.Combine(output, file))) throw new InvalidOperationException("Required authoritative compiler artifact missing: " + file);
            await File.WriteAllTextAsync(Path.Combine(output, "defines.h"), $"#pragma once\n#define PROGRAM_MD5 \"{programMd5}\"\n", token);
            foreach (var header in headerSources)
            {
                var target = Path.Combine(output, "strucpp_runtime", "include", header.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(header.Value, target);
            }
            foreach (var license in new[] { "COPYING", "COPYING.RUNTIME", "LICENSE" }) File.Copy(Path.Combine(distribution, license), Path.Combine(output, license));
            File.Copy(compatibilityLicense, Path.Combine(output, "RUNTIME-COMPAT-LICENSE"));
            var symbolHash = await FileHashAsync(Path.Combine(output, "debug-map.json"), token);
            var inventory = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "build-identity.json") continue; // Compiler-only manifest is superseded by package identity.
                inventory.Add(Path.GetRelativePath(output, file).Replace('\\', '/'), await FileHashAsync(file, token));
            }
            File.Delete(Path.Combine(output, "build-identity.json"));
            var buildHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { InputIdentity = inputIdentity, Files = inventory }))));
            var manifestJson = JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, snapshot.ProjectId, snapshot.SourceHash, snapshot.HardwareHash,
                CompilerVersion = compilation.CompilerVersion, RuntimeVersion = "4.3.0", RuntimeCommit,
                BuildHash = buildHash, SymbolTableHash = symbolHash, ProgramMd5 = programMd5,
                RuntimeHeaderHashes = runtimeHeaderHashes, CompilerLibraryHashes = libraryHashes,
                Configuration = "CONFIG0", Resource = "RES0", Instance = "INSTANCE0", Tasks = tasks,
                ArtifactKind = "isolated-runtime-staging-candidate", LinuxCompilationVerified = false, SafeForDirectUpload = false,
                RuntimeLinkVerified = false, CommercialRedistributionCleared = false, UnmarkedRuntimeHeaders = unmarkedHeaders,
                RuntimeHeaderRedistributionCleared = true,
                RuntimeHeaderReplacements = replacements.Select(pair => new { Path = pair.Key, Sha256 = pair.Value, License = "MIT" }).ToArray(),
                LicensingAuditBlocker = "", CommercialProductAuditStatus = "pending",
                MotionInterfaceVersion = pathAssets.Length == 0 ? null : "workbench-continuous-v1", Files = inventory
            }, new JsonSerializerOptions { WriteIndented = true });
            if (cycle is not null || axisUsed || communicationUsed || controlUsed)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(manifestJson)!;
                if (cycle is not null) node["MotionCycleInterfaceVersion"] = "workbench-motion-cycle-v1";
                if (axisUsed) node["AxisInterfaceVersion"] = "workbench-axis-v1";
                if (communicationUsed) node["CommunicationInterfaceVersion"] = CommunicationBinding.Interface;
                if (controlUsed) node["ControlLibraryVersion"] = "1.0.0";
                manifestJson = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }
            await File.WriteAllTextAsync(Path.Combine(output, "build-manifest.json"), manifestJson, token);
            token.ThrowIfCancellationRequested();
            var packagePath = Path.Combine(packageRoot, "program.zip");
            using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.GetFiles(output, "*", SearchOption.AllDirectories)
                    .OrderBy(file => Path.GetRelativePath(output, file).Replace('\\', '/'), StringComparer.Ordinal))
                {
                    var entry = archive.CreateEntry(Path.GetRelativePath(output, file).Replace('\\', '/'), CompressionLevel.Optimal);
                    entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    entry.ExternalAttributes = 0;
                    await using var source = File.OpenRead(file);
                    await using var target = entry.Open();
                    await source.CopyToAsync(target, token);
                }
            }
            return new(compilation, packagePath, buildHash, symbolHash);
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            var failed = compilation is null
                ? new BuildResult(false, [new("", 0, 0, "error", error.Message)], error.Message, "", snapshot.SourceHash, "")
                : compilation with { Success = false, Diagnostics = [.. compilation.Diagnostics, new("", 0, 0, "error", error.Message)], Log = compilation.Log + "\n" + error.Message };
            return new(failed, "", "", "");
        }
    }

    private static async Task<string> FileHashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }
}

