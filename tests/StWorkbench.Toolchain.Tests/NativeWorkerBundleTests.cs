using System.Security.Cryptography;
using System.Text;
using StWorkbench.Toolchain;

static class NativeWorkerBundleTests
{
    public static async Task RunAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "st-native-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bundle = await NativeWorkerBundle.CaptureAsync(Path.GetFullPath("linux/native"), ["st-axis-worker", "st-communication-worker", "st-path-worker"], default);
            if (bundle.SourcePaths.Count == 0 || bundle.SourcePaths.Any(p => p.EndsWith(".py", StringComparison.Ordinal))) throw new Exception("Native bundle must contain only approved native sources.");
            var first = await bundle.WriteAsync(temporary, default);
            var canonical = string.Concat(first.Sources.Select(p => p.Key + "\0" + p.Value + "\n"));
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
            if (first.SourceBundleSha256 != expected) throw new Exception("Source bundle canonical identity differs.");
            foreach (var pair in first.Sources)
            {
                var bytes = await File.ReadAllBytesAsync(Path.Combine(temporary, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
                if (Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() != pair.Value) throw new Exception("Bundle file hash mismatch.");
            }
            if (File.ReadAllText(Path.Combine(temporary, "native/CMakeLists.txt")).Contains("tests/", StringComparison.Ordinal)) throw new Exception("Production bundle must not require test source.");
            var captured = await NativeWorkerBundle.CaptureAsync(Path.Combine(temporary, "native"), ["st-axis-worker"], default);
            var capturedIdentity = captured.Identity().SourceBundleSha256;
            await File.AppendAllTextAsync(Path.Combine(temporary, "native/src/common.cpp"), "\n// changed after capture\n");
            if (captured.Identity().SourceBundleSha256 != capturedIdentity) throw new Exception("Native source capture changed after caller modified source files.");
            var failed = false;
            try { await NativeWorkerBundle.CaptureAsync(Path.GetFullPath("linux/native"), ["../unapproved"], default); }
            catch (InvalidDataException) { failed = true; }
            if (!failed) throw new Exception("Unknown native target accepted.");
            Console.WriteLine("Native worker source bundle identities and target admission passed.");
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
}
