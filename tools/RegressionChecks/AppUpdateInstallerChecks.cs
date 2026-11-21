using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.RegressionChecks;

internal static partial class Program
{
    private static readonly string[] UpdateRequiredFiles =
        ["NimCap.Screenshot.exe", "NimCap.Screenshot.dll", "NimCap.Screenshot.deps.json", "NimCap.Screenshot.runtimeconfig.json", "coreclr.dll", "hostfxr.dll"];

    private static async Task AppUpdateInstallerChecks()
    {
        var root = Path.GetFullPath(Path.Combine(".artifacts", "update-installer-checks", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string Archive(string id, string prefix = "nimcap-screenshot-v1.0.9-win-x64/", string? extra = null, bool missingRuntime = false)
        {
            var path = Path.Combine(root, id + ".zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var name in UpdateRequiredFiles.Where(name => !missingRuntime || name != "coreclr.dll").Select(name => prefix + name)
                .Concat(extra is null ? [] : new[] { prefix + extra }))
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write("fixture " + name);
            }
            return path;
        }
        var archive = Archive("valid");
        var update = AppUpdateInstaller.ExtractPackage(archive, Path.Combine(root, "valid"), default);
        Require(update.Files.Length == 6 && File.Exists(Path.Combine(update.Stage, "NimCap.Screenshot.exe")), "Wrapped update package not extracted.");
        var flat = Archive("flat", "");
        Require(AppUpdateInstaller.ExtractPackage(flat, Path.Combine(root, "flat"), default).Files.Length == 6, "Flat update package not extracted.");
        var portableArchive = Archive("portable", "Program/");
        using (var zip = ZipFile.Open(portableArchive, ZipArchiveMode.Update))
        {
            foreach (var name in new[] { "NimCap.Screenshot.exe", "portable", "README.md", "Data/.gitkeep" })
            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("root " + name);
        }
        var portable = AppUpdateInstaller.ExtractPackage(portableArchive, Path.Combine(root, "portable"), default);
        Require(portable.Files.Length == 6 && File.Exists(Path.Combine(portable.Stage, "NimCap.Screenshot.exe")),
            "Portable update package not extracted under Program/.");
        Require(!File.Exists(Path.Combine(portable.Stage, "portable")),
            "Portable marker leaked into the update stage.");
        foreach (var (name, extra) in new[] { ("portable-data", "Data/settings.json"), ("portable-unknown", "extra.dll") })
        {
            var bad = Archive(name, "Program/", extra: null);
            using (var zip = ZipFile.Open(bad, ZipArchiveMode.Update))
            using (var writer = new StreamWriter(zip.CreateEntry(extra).Open())) writer.Write("outside Program");
            var rejected = false;
            try { AppUpdateInstaller.ExtractPackage(bad, Path.Combine(root, name), default); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Unsafe portable update archive accepted: " + name);
        }
        foreach (var (name, extra) in new[] { ("traversal", "../outside.dll"), ("absolute", "/evil.dll"),
            ("settings", "settings.json"), ("model", "models/model.onnx"), ("data", "data-location.json"),
            ("alternate", "file.dll:extra"), ("reserved", "CON.dll"), ("duplicate", "nimcap.screenshot.exe") })
        {
            var bad = Archive(name, extra: extra);
            var rejected = false;
            try { AppUpdateInstaller.ExtractPackage(bad, Path.Combine(root, name), default); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Unsafe update archive accepted: " + name);
        }
        var incomplete = Archive("missing", missingRuntime: true);
        var missingRejected = false;
        try { AppUpdateInstaller.ExtractPackage(incomplete, Path.Combine(root, "missing"), default); }
        catch (InvalidDataException) { missingRejected = true; }
        Require(missingRejected, "Incomplete runtime accepted.");
        var checksum = new string('A', 64);
        Require(AppUpdateInstaller.ReadChecksum(checksum + "  nimcap-screenshot-v1.0.9-win-x64.zip\n", "nimcap-screenshot-v1.0.9-win-x64.zip") == checksum,
            "SHA256SUMS not parsed.");
        Require(AppUpdateInstaller.ReadChecksum(checksum + "  Other.zip", "nimcap-screenshot-v1.0.9-win-x64.zip") is null, "Checksum for another file used.");
        var release = new AppRelease(new Version(1, 0, 9, 0), "v1.0.9", "", new Uri(AppUpdateService.ReleasesUrl),
            new Uri(AppUpdateService.ReleasesUrl + "/download/v1.0.9/nimcap-screenshot-v1.0.9-win-x64.zip"));
        using (var client = new HttpClient(new UpdatePackageHandler(File.ReadAllBytes(archive))))
        {
            var rejected = false;
            try { await AppUpdateInstaller.PrepareAsync(release, new UpdateProgress(), default, client, Path.Combine(root, "no-checksum")); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Unsigned checksum metadata silently accepted.");
        }
        using (var client = new HttpClient(new UpdatePackageHandler(File.ReadAllBytes(archive))))
        {
            var rejected = false;
            var work = Path.Combine(root, "bad-hash");
            try { await AppUpdateInstaller.PrepareAsync(release with { Sha256 = new string('0', 64) }, new UpdateProgress(), default, client, work); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected && !Directory.EnumerateDirectories(work).Any(), "Hash mismatch accepted or partial download not cleaned up.");
        }
        using (var client = new HttpClient(new UpdatePackageHandler(File.ReadAllBytes(archive))))
        {
            var rejected = false;
            using var stream = File.OpenRead(archive);
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            try { await AppUpdateInstaller.PrepareAsync(release with { Sha256 = hash }, new UpdateProgress(), default, client, Path.Combine(root, "wrong-version")); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Executable without expected version accepted.");
        }
        var verifiedArchive = Archive("verified");
        using (var zip = ZipFile.Open(verifiedArchive, ZipArchiveMode.Update))
        {
            zip.GetEntry("nimcap-screenshot-v1.0.9-win-x64/NimCap.Screenshot.exe")!.Delete();
            zip.CreateEntryFromFile(Environment.ProcessPath!, "nimcap-screenshot-v1.0.9-win-x64/NimCap.Screenshot.exe");
        }
        var verifiedBytes = File.ReadAllBytes(verifiedArchive);
        var verifiedHash = Convert.ToHexString(SHA256.HashData(verifiedBytes));
        var verifiedVersion = AppUpdateService.Normalize(Version.Parse(FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion!));
        using (var client = new HttpClient(new UpdatePackageHandler(verifiedBytes, verifiedHash + "  nimcap-screenshot-v1.0.9-win-x64.zip")))
        {
            var prepared = await AppUpdateInstaller.PrepareAsync(release with
            {
                Version = verifiedVersion,
                Checksums = new Uri(AppUpdateService.ReleasesUrl + "/download/v1.0.9/SHA256SUMS.txt")
            }, new UpdateProgress(), default, client, Path.Combine(root, "verified-download"));
            Require(File.Exists(Path.Combine(prepared.Stage, "NimCap.Screenshot.exe")), "Verified package was not prepared.");
        }
        using (var client = new HttpClient(new UpdatePackageHandler(verifiedBytes)))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = false;
            try { await AppUpdateInstaller.PrepareAsync(release with { Sha256 = verifiedHash }, new UpdateProgress(), cancellation.Token, client, Path.Combine(root, "cancelled")); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Cancelled update download continued.");
        }
        await RunUpdateHelperFixture(root, "install", locked: false);
        await RunUpdateHelperFixture(root, "rollback", locked: true);
        await RunUpdateHelperFixture(root, "tampered", locked: false, tamper: true);
#if UPDATE_CHECKS_ONLY
        await RunUpdateHelperFixture(root, "restart", locked: false, restart: true);
#endif
        Console.WriteLine("PASS: verified download/extraction, checksum fallback, cancellation, ZIP path/data protection, actual helper installation, parent-exit wait, restart and rollback of overwritten/new files.");
    }

    private static async Task RunUpdateHelperFixture(string root, string name, bool locked, bool tamper = false, bool restart = false)
    {
        var work = Path.Combine(root, name);
        var target = Path.Combine(work, "application");
        var stage = Path.Combine(work, "staged");
        var backup = Path.Combine(work, "backup");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(stage);
        File.WriteAllText(Path.Combine(target, "NimCap.Screenshot.exe"), "old executable");
        File.WriteAllText(Path.Combine(target, "b.dll"), "old library");
        File.WriteAllText(Path.Combine(target, "settings.json"), "user settings");
        Directory.CreateDirectory(Path.Combine(target, "models"));
        File.WriteAllText(Path.Combine(target, "models", "local.onnx"), "user model");
        if (restart)
            foreach (var path in Directory.GetFiles(AppContext.BaseDirectory).Where(path => Path.GetExtension(path) is ".dll" or ".json"))
                File.Copy(path, Path.Combine(target, Path.GetFileName(path)));
        var files = new[] { "NimCap.Screenshot.exe", "new.dll", "b.dll" }.Select(relative =>
        {
            if (restart && relative == "NimCap.Screenshot.exe") File.Copy(Environment.ProcessPath!, Path.Combine(stage, relative));
            else File.WriteAllText(Path.Combine(stage, relative), "new " + relative);
            return new { Relative = relative, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(stage, relative)))) };
        }).ToArray();
        if (tamper) File.AppendAllText(Path.Combine(stage, "b.dll"), "tampered");
        var plan = Path.Combine(work, "plan.json");
        using var parent = restart ? Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--update-parent" } }) : null;
        File.WriteAllText(plan, JsonSerializer.Serialize(new
        {
            Target = target, Stage = stage, Backup = backup, Files = files, ParentId = parent?.Id ?? 0,
            ParentStartTicks = parent?.StartTime.ToUniversalTime().Ticks ?? 0,
            Ready = Path.Combine(work, "ready"), Abort = Path.Combine(work, "abort"), Result = Path.Combine(work, "result.json")
        }));
        var script = Path.Combine(work, "install.ps1");
        using (var resource = typeof(AppUpdateInstaller).Assembly.GetManifestResourceStream("NimCap.Screenshot.AppUpdate.ps1")!)
        using (var output = File.Create(script)) resource.CopyTo(output);
        using var fileLock = locked ? new FileStream(Path.Combine(target, "b.dll"), FileMode.Open, FileAccess.Read, FileShare.Read) : null;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Plan", plan }) start.ArgumentList.Add(argument);
        if (!restart) start.ArgumentList.Add("-TestMode");
        else start.Environment["NIMCAP_SCREENSHOT_UPDATE_RESTART_MARKER"] = Path.Combine(work, "restart-marker");
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (restart)
        {
            for (var i = 0; i < 50 && !File.Exists(Path.Combine(work, "ready")) && !process.HasExited; i++) await Task.Delay(100, timeout.Token);
            Require(parent is not null && !parent.HasExited && File.ReadAllText(Path.Combine(target, "NimCap.Screenshot.exe")) == "old executable",
                "Update touched a running application's files before it exited.");
        }
        await process.WaitForExitAsync(timeout.Token);
        var stderr = await process.StandardError.ReadToEndAsync();
        var result = File.Exists(Path.Combine(work, "result.json")) ? File.ReadAllText(Path.Combine(work, "result.json")) : stderr;
        Require(File.ReadAllText(Path.Combine(target, "settings.json")) == "user settings" &&
            File.ReadAllText(Path.Combine(target, "models", "local.onnx")) == "user model", "Update overwrote user data.");
        if (locked || tamper)
        {
            Require(process.ExitCode == 1, "Failed update did not report failure: " + result);
            Require(File.ReadAllText(Path.Combine(target, "NimCap.Screenshot.exe")) == "old executable" &&
                File.ReadAllText(Path.Combine(target, "b.dll")) == "old library" && !File.Exists(Path.Combine(target, "new.dll")),
                "Update rollback left modified files: " + result);
            if (locked) Require(File.Exists(Path.Combine(backup, "NimCap.Screenshot.exe")), "No rollback backup.");
        }
        else
        {
            Require(process.ExitCode == 0 && (restart || File.ReadAllText(Path.Combine(target, "NimCap.Screenshot.exe")) == "new NimCap.Screenshot.exe") &&
                File.ReadAllText(Path.Combine(target, "new.dll")) == "new new.dll", "Update did not replace program files: " + result);
            Require(File.ReadAllText(Path.Combine(backup, "NimCap.Screenshot.exe")) == "old executable", "Old version backup missing.");
            if (restart)
            {
                for (var i = 0; i < 100 && !File.Exists(Path.Combine(work, "restart-marker")); i++) await Task.Delay(100, timeout.Token);
                Require(File.Exists(Path.Combine(work, "restart-marker")), "Updater did not launch the new version after replacing files.");
            }
        }
    }

    private sealed class UpdateProgress : IProgress<int> { public void Report(int value) { } }
    private sealed class UpdatePackageHandler(byte[] package, string? checksumList = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt") && checksumList is not null
                    ? new StringContent(checksumList) : new ByteArrayContent(package)
            });
    }
}
