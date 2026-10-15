using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StWorkbench.Core;

namespace StWorkbench.Toolchain;

public sealed record BuildResult(bool Success, IReadOnlyList<Diagnostic> Diagnostics, string Log,
    string OutputDirectory, string SourceHash, string CompilerVersion);

/// <summary>Runs the standalone STruCpp CLI. Outputs are local C++ artifacts, not a deployable OpenPLC package.</summary>
public sealed class ExternalCompiler
{
    private readonly TimeSpan timeout;
    private readonly string? expectedCompilerVersion;
    private readonly int maximumLogCharacters;
    private readonly string[] executableArguments;
    private readonly string outputFileName;
    private static readonly Regex LocatedDiagnostic = new(@"^(.+):(\d+):(\d+):\s*(error|warning):\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public ExternalCompiler(TimeSpan? timeout = null, string? expectedCompilerVersion = null, int maximumLogCharacters = 262144,
        IReadOnlyList<string>? executableArguments = null, string outputFileName = "program.cpp")
    {
        this.timeout = timeout ?? TimeSpan.FromMinutes(2);
        if (this.timeout <= TimeSpan.Zero || this.timeout > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumLogCharacters < 256) throw new ArgumentOutOfRangeException(nameof(maximumLogCharacters));
        this.expectedCompilerVersion = expectedCompilerVersion;
        this.maximumLogCharacters = maximumLogCharacters;
        this.executableArguments = executableArguments?.ToArray() ?? [];
        if (Path.GetFileName(outputFileName) != outputFileName || !outputFileName.EndsWith(".cpp", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be a C++ basename.", nameof(outputFileName));
        this.outputFileName = outputFileName;
    }

    public async Task<BuildResult> CompileAsync(BuildSnapshot snapshot, string executable, string buildRoot, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        token.ThrowIfCancellationRequested();
        // Copy source records before the first await; later editor mutations cannot change these inputs.
        var sources = snapshot.Sources.ToArray();
        var sourceHash = snapshot.SourceHash;
        var hardwareHash = snapshot.HardwareHash;
        var projectId = snapshot.ProjectId;
        var diagnostics = new List<Diagnostic>();
        var reportedCompilerError = false;
        var log = new BoundedLog(maximumLogCharacters);
        var outputDirectory = "";
        var version = "";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        BuildResult Result(bool success) => new(success, diagnostics.ToArray(), log.ToString(), outputDirectory, sourceHash, version);
        void Error(string message)
        {
            diagnostics.Add(new("", 0, 0, "error", message));
            log.Append(message + Environment.NewLine);
        }
        try
        {
            var workspace = Path.Combine(Path.GetFullPath(buildRoot), Guid.NewGuid().ToString("N"));
            var inputDirectory = Path.Combine(workspace, "inputs");
            outputDirectory = Path.Combine(workspace, "output");
            var inputPaths = new List<string>();
            var sourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sources.Length == 0) { Error("No ST sources to compile."); return Result(false); }
            foreach (var source in sources)
            {
                var relative = source.RelativePath.Replace('\\', '/');
                if (Path.IsPathRooted(relative) || relative.Split('/').Any(p => p is ".." or "." or "") || relative.Contains(':') || !relative.EndsWith(".st", StringComparison.OrdinalIgnoreCase))
                { Error("Invalid source path: " + source.RelativePath); return Result(false); }
                var path = Path.GetFullPath(Path.Combine(inputDirectory, relative));
                if (!path.StartsWith(inputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !sourceMap.TryAdd(path, source.RelativePath))
                { Error("Duplicate or escaping source path: " + source.RelativePath); return Result(false); }
                inputPaths.Add(path);
            }
            Directory.CreateDirectory(outputDirectory);
            foreach (var (source, path) in sources.Zip(inputPaths))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, source.Text, new UTF8Encoding(false), deadline.Token);
            }
            // STruCpp 0.7.0 reports basenames rather than the absolute paths supplied on its CLI.
            // Only unique basenames can safely map back to an editor document.
            var uniqueBasenames = sourceMap.GroupBy(pair => Path.GetFileName(pair.Key), StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Value, StringComparer.OrdinalIgnoreCase);

            void ParseLine(string line)
            {
                var match = LocatedDiagnostic.Match(line);
                if (!match.Success || !int.TryParse(match.Groups[2].Value, out var row) || !int.TryParse(match.Groups[3].Value, out var column)) return;
                var file = match.Groups[1].Value;
                var full = Path.GetFullPath(file, workspace);
                if (sourceMap.TryGetValue(full, out var original)) file = original;
                else if (file == Path.GetFileName(file) && uniqueBasenames.TryGetValue(file, out original)) file = original;
                lock (diagnostics)
                {
                    reportedCompilerError |= match.Groups[4].Value.Equals("error", StringComparison.OrdinalIgnoreCase);
                    if (diagnostics.Count < 1000) diagnostics.Add(new(file, row, column, match.Groups[4].Value.ToLowerInvariant(), match.Groups[5].Value));
                }
            }

            // Native standalone compiler required. .cmd wrappers are deliberately not sent through a shell.
            var resolvedExecutable = executable;
            if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar)) resolvedExecutable = Path.GetFullPath(executable);
            var versionOutput = new BoundedLog(4096);
            var versionExit = await RunAsync(resolvedExecutable, [.. executableArguments, "--version"], workspace, log, versionOutput, null, deadline.Token);
            version = versionOutput.ToString().Trim();
            if (versionExit != 0 || string.IsNullOrWhiteSpace(version)) { Error("Compiler version query failed."); return Result(false); }
            if (expectedCompilerVersion is not null && !string.Equals(expectedCompilerVersion, version, StringComparison.Ordinal))
            { Error($"Compiler version mismatch: expected '{expectedCompilerVersion}', actual '{version}'."); return Result(false); }
            var cpp = Path.Combine(outputDirectory, outputFileName);
            var header = Path.ChangeExtension(cpp, ".hpp");
            var exit = await RunAsync(resolvedExecutable, [.. executableArguments, .. inputPaths, "-o", cpp], workspace, log, null, ParseLine, deadline.Token);
            if (exit != 0) { Error($"Compiler exited with code {exit}."); return Result(false); }
            if (reportedCompilerError)
            {
                if (!diagnostics.Any(d => d.Severity == "error")) Error("Compiler reported errors after the diagnostic display limit; inspect the compiler log.");
                return Result(false);
            }
            if (!File.Exists(cpp) || !File.Exists(header) || new FileInfo(cpp).Length == 0 || new FileInfo(header).Length == 0)
            { Error("Compiler exited successfully but generated C++/header files are missing or empty."); return Result(false); }
            var generatedFiles = new Dictionary<string, string>();
            foreach (var file in new[] { cpp, header })
            {
                await using var stream = File.OpenRead(file);
                generatedFiles.Add(Path.GetFileName(file), Convert.ToHexString(await SHA256.HashDataAsync(stream, deadline.Token)));
            }
            var manifest = new
            {
                SchemaVersion = 1, ProjectId = projectId, SourceHash = sourceHash, HardwareHash = hardwareHash,
                CompilerVersion = version, CompilerExecutable = resolvedExecutable,
                ArtifactKind = "local-strucpp-cpp", RuntimeCompatibility = "unverified",
                SymbolTableHash = (string?)null, MotionInterfaceVersion = (string?)null, GeneratedFiles = generatedFiles
            };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "build-identity.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
            return Result(true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { Error("External compiler timed out."); return Result(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or ArgumentException or InvalidOperationException)
        { Error("External compiler failed: " + ex.Message); return Result(false); }
    }

    private static async Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string workspace,
        BoundedLog log, BoundedLog? stdoutCapture, Action<string>? parse, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = workspace };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        var stdout = DrainAsync(process.StandardOutput, log, stdoutCapture, parse, cancellation);
        var stderr = DrainAsync(process.StandardError, log, null, parse, cancellation);
        try
        {
            await process.WaitForExitAsync(cancellation);
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            // Waits and stream reads stay bounded even if descendants retain inherited output handles.
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static async Task DrainAsync(StreamReader reader, BoundedLog log, BoundedLog? capture,
        Action<string>? parse, CancellationToken cancellation)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            var chunk = new string(buffer, 0, count);
            log.Append(chunk); capture?.Append(chunk);
            if (parse is null) continue;
            foreach (var character in chunk)
            {
                if (character == '\n') { parse(line.ToString().TrimEnd('\r')); line.Clear(); }
                else if (line.Length < 8192) line.Append(character);
            }
        }
        if (line.Length > 0) parse?.Invoke(line.ToString());
    }

    private sealed class BoundedLog(int limit)
    {
        private readonly StringBuilder text = new();
        private bool truncated;
        public void Append(string value)
        {
            lock (text)
            {
                var available = Math.Max(0, limit - text.Length);
                text.Append(value.AsSpan(0, Math.Min(value.Length, available)));
                truncated |= value.Length > available;
            }
        }
        public override string ToString()
        {
            lock (text)
            {
                const string marker = "\n[output truncated]\n";
                return truncated ? text.ToString(0, Math.Min(text.Length, limit - marker.Length)) + marker : text.ToString();
            }
        }
    }
}
