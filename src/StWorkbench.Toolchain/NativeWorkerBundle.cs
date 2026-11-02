using System.Security.Cryptography;
using System.Text;

namespace StWorkbench.Toolchain;

public sealed record NativeWorkerIdentity(string InterfaceVersion, string SourceBundleSha256,
    IReadOnlyDictionary<string, string> Sources, IReadOnlyList<string> Targets);

/// <summary>Captures immutable native worker source bytes. Linux records the independently compiled ELF identities.</summary>
public sealed class NativeWorkerBundle
{
    public const string InterfaceVersion = "workbench-linux-native-v1";
    private readonly SortedDictionary<string, byte[]> files;
    private readonly string[] targets;
    public IReadOnlyCollection<string> SourcePaths => files.Keys;
    private NativeWorkerBundle(SortedDictionary<string, byte[]> files, string[] targets) { this.files = files; this.targets = targets; }
    public static async Task<NativeWorkerBundle> CaptureAsync(string directory, IEnumerable<string> targets, CancellationToken token)
    {
        var selected = targets.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (selected.Length == 0 || selected.Any(t => t is not ("st-axis-worker" or "st-communication-worker" or "st-path-worker")))
            throw new InvalidDataException("Unsupported native worker target.");
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var relative = new[] { "LICENSE", "include/stworkbench/common.hpp", "include/stworkbench/sha256.hpp", "src/common.cpp" }.ToList();
        if (selected.Contains("st-axis-worker")) relative.Add("src/axis_worker.cpp");
        if (selected.Contains("st-path-worker")) relative.Add("src/path_worker.cpp");
        if (selected.Contains("st-communication-worker")) relative.AddRange(["src/communication_worker.cpp", "src/communication_protocol.hpp", "src/communication_channel.hpp", "src/communication_engine.hpp"]);
        foreach (var name in relative)
        {
            var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Native source missing or unsafe: " + name);
            files.Add("native/" + name, await File.ReadAllBytesAsync(path, token));
        }
        const string preamble = "cmake_minimum_required(VERSION 3.20)\nproject(st_workbench_workers LANGUAGES CXX)\nif(NOT UNIX)\nmessage(FATAL_ERROR \"Linux target required\")\nendif()\nset(CMAKE_CXX_STANDARD 20)\nset(CMAKE_CXX_STANDARD_REQUIRED ON)\nset(CMAKE_CXX_EXTENSIONS OFF)\nfind_package(Threads REQUIRED)\nfind_package(nlohmann_json 3.2.0 REQUIRED)\nadd_library(st-native-common src/common.cpp)\ntarget_include_directories(st-native-common PUBLIC include)\ntarget_link_libraries(st-native-common PUBLIC nlohmann_json::nlohmann_json Threads::Threads)\n";
        var cmake = new StringBuilder(preamble);
        foreach (var target in selected)
        {
            var source = target switch { "st-axis-worker" => "axis_worker.cpp", "st-path-worker" => "path_worker.cpp", _ => "communication_worker.cpp" };
            cmake.Append("add_executable(").Append(target).Append(" src/").Append(source).Append(")\ntarget_link_libraries(").Append(target).Append(" PRIVATE st-native-common)\n");
        }
        files.Add("native/CMakeLists.txt", Encoding.UTF8.GetBytes(cmake.ToString()));
        return new(files, selected);
    }
    public NativeWorkerIdentity Identity()
    {
        var inventory = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in files) inventory.Add(pair.Key, Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant());
        var canonical = string.Concat(inventory.Select(pair => pair.Key + "\0" + pair.Value + "\n"));
        return new(InterfaceVersion, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant(), inventory, targets);
    }
    public async Task<NativeWorkerIdentity> WriteAsync(string output, CancellationToken token)
    {
        foreach (var pair in files)
        {
            var target = Path.Combine(output, pair.Key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, pair.Value, token);
        }
        return Identity();
    }
}
