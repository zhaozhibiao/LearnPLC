using StWorkbench.Core;
using StWorkbench.RuntimeMigration;

try
{

var archive = Path.GetFullPath("artifacts/complete-retest-20261030/formal-engineering-archive.json");
if (args.SequenceEqual(new[] { "--inspect" }))
{
    var json = await File.ReadAllTextAsync(archive);
    var a = System.Text.Json.JsonSerializer.Deserialize<StWorkbench.Toolchain.EngineeringArchive>(json)!;
    var captured = ProjectValidator.Capture(a.Project, a.Sources);
    var legacyTasks = a.Project.Tasks.Select(t => new { t.Name, t.ProgramId, t.IntervalMilliseconds, t.Enabled }).ToArray();
    var legacyHash = ProjectValidator.Hash(System.Text.Json.JsonSerializer.Serialize(new { a.Project.Id, a.Project.Name, Sources = captured.Sources, Tasks = legacyTasks }));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { a.SourceHash, CurrentSourceHash = captured.SourceHash, LegacySourceHash = legacyHash, a.HardwareHash, CurrentHardwareHash = captured.HardwareHash }));
    return 0;
}
if (args.SequenceEqual(new[] { "--self-test" })) { await ProfileTests.RunAsync(archive); return 0; }
if (args.SequenceEqual(new[] { "--build" }) || args.SequenceEqual(new[] { "--build-smoke" }) || args is ["--build-speed", _])
{
    var smoke = args[0] == "--build-smoke";
    var rpm = args[0] == "--build-speed" ? int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
    var source = StWorkbench.Toolchain.EngineeringArchive.Read(await File.ReadAllTextAsync(archive));
    var migrated = rpm != 0 ? MigrationProfile.CreateSpeedTest(source, rpm) : smoke ? MigrationProfile.CreateSmoke(source) : MigrationProfile.Create(source);
    var runtime = EtherCatRuntimeCompiler.Compile(migrated.Project);
    if (!runtime.ReadyForRuntime) throw new InvalidDataException(string.Join("; ", runtime.Blockers));
    if (rpm == 0) ContractChecks.Verify(migrated, runtime);
    var output = Path.GetFullPath(rpm != 0 ? $"examples/is620n-runtime-speed-{rpm}" : smoke ? "examples/is620n-runtime-acceptance" : "examples/is620n-runtime-migrated");
    Directory.CreateDirectory(output);
    await new ProjectStore().SaveAsync(Path.Combine(output, "IS620N-Runtime-Migrated.stproj"), migrated.Project, migrated.Sources);
    var hardware = await new StWorkbench.Toolchain.HardwarePackageBuilder().BuildAsync(migrated.Project, "artifacts/runtime-migration-packages");
    if (!hardware.Success) throw new InvalidDataException(string.Join("; ", hardware.Errors));
    var snapshot = ProjectValidator.Capture(migrated.Project, migrated.Sources);
    var package = await new StWorkbench.Toolchain.PackageBuilder().BuildAsync(snapshot,
        migrated.Project.Items.Where(i => i.Kind == ItemKind.Program).ToDictionary(i => i.Id, i => i.Name),
        "node", Path.GetFullPath("tools/compiler-adapter/compile.mjs"), Path.GetFullPath(".tools/strucpp-0.7.0/strucpp"), "artifacts/runtime-migration-packages");
    if (!package.Success) throw new InvalidDataException(System.Text.Json.JsonSerializer.Serialize(package.Compilation));
    var report = System.Text.Json.JsonSerializer.Serialize(new { ProjectPath = output, hardware.PackagePath, hardware.BuildHash, hardware.HardwareHash,
        ProgramPackage = package.PackagePath, ProgramBuildHash = package.BuildHash, package.SymbolTableHash }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(rpm != 0 ? $"artifacts/complete-retest-20261030/runtime-speed-{rpm}-packages.json" : smoke ? "artifacts/complete-retest-20261030/runtime-acceptance-packages.json" : "artifacts/complete-retest-20261030/runtime-migration-packages.json", report);
    Console.WriteLine(report);
    return 0;
}
throw new ArgumentException("Use --inspect, --self-test or --build.");
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
