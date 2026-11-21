using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NimCap.Screenshot.Services;

internal sealed record PreparedAppUpdate(string Directory, string Stage, string[] Files);

internal static class AppUpdateInstaller
{
    private const long MaximumDownload = 1536L * 1024 * 1024;
    private const long MaximumExpanded = 4L * 1024 * 1024 * 1024;
    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "models", "tessdata", "downloads", "history", "diagnostics", "cache", "plugins", "settings.json",
        "data-location.json", ".git", ".codex", ".agents", ".aws", ".artifacts", "recordings", "screenshots"
    };

    internal static async Task<PreparedAppUpdate> PrepareAsync(AppRelease release, IProgress<int> progress,
        CancellationToken cancellation, HttpClient? client = null, string? workRoot = null)
    {
        if (release.Download is null) throw new InvalidDataException("No Windows x64 update package.");
        using var ownedClient = client is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromMinutes(15) }
            : null;
        client ??= ownedClient!;
        var checksum = release.Sha256;
        if (checksum is null && release.Checksums is not null)
        {
            using var response = await GetAsync(client, release.Checksums, cancellation).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 64 * 1024) throw new InvalidDataException("Checksum list is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await CopyBounded(input, buffer, 64 * 1024, null, cancellation).ConfigureAwait(false);
            checksum = ReadChecksum(System.Text.Encoding.UTF8.GetString(buffer.ToArray()), Path.GetFileName(release.Download.LocalPath));
        }
        if (checksum is null || !Regex.IsMatch(checksum, "\\A[0-9a-fA-F]{64}\\z"))
            throw new InvalidDataException("The release does not provide a valid SHA256 checksum.");
        var directory = Path.Combine(workRoot ?? Path.Combine(Path.GetTempPath(), "NimCap.Screenshot-updates"), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        try
        {
            var archive = Path.Combine(directory, "update.zip");
            using (var response = await GetAsync(client, release.Download, cancellation).ConfigureAwait(false))
            {
                var length = response.Content.Headers.ContentLength;
                if (length is <= 0 or > MaximumDownload) throw new InvalidDataException("Invalid update package size.");
                await using var input = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
                await using var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                var previousPercent = -2;
                await CopyBounded(input, output, MaximumDownload,
                    new ProgressCallback(bytes =>
                    {
                        var percent = length > 0 ? (int)Math.Min(100, bytes * 100 / length.Value) : -1;
                        if (percent != previousPercent) { previousPercent = percent; progress.Report(percent); }
                    }), cancellation).ConfigureAwait(false);
            }
            await using (var input = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation).ConfigureAwait(false));
                if (!actual.Equals(checksum, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update package SHA256 mismatch.");
            }
            cancellation.ThrowIfCancellationRequested();
            var prepared = ExtractPackage(archive, directory, cancellation);
            var productVersion = FileVersionInfo.GetVersionInfo(Path.Combine(prepared.Stage, "NimCap.Screenshot.exe")).FileVersion;
            if (!Version.TryParse(productVersion, out var version) || AppUpdateService.Normalize(version) != release.Version)
                throw new InvalidDataException("The update executable version does not match the release.");
            return prepared;
        }
        catch { Cleanup(directory); throw; }
    }

    internal static string? ReadChecksum(string text, string package)
    {
        foreach (var line in text.TrimStart('\uFEFF').Split('\n'))
        {
            var match = Regex.Match(line.Trim(), @"\A([0-9a-fA-F]{64})\s+\*?(.+)\z");
            if (match.Success && match.Groups[2].Value == package) return match.Groups[1].Value;
        }
        return null;
    }

    internal static PreparedAppUpdate ExtractPackage(string archive, string directory, CancellationToken cancellation)
    {
        var stage = Path.Combine(directory, "stage");
        System.IO.Directory.CreateDirectory(stage);
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count is 0 or > 15000) throw new InvalidDataException("Invalid update file count.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<(ZipArchiveEntry Entry, string Name)>();
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            ValidateRelativePath(name.TrimEnd('/'));
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Symbolic links are not allowed.");
            if (name.EndsWith('/')) continue;
            total = checked(total + entry.Length);
            if (total > MaximumExpanded || !paths.Add(name)) throw new InvalidDataException("Oversized or duplicate update files.");
            entries.Add((entry, name));
        }
        // Portable packages keep the real application under Program/ and add a launcher at the archive root.
        var portableExecutable = entries.FirstOrDefault(x => x.Name.Equals("Program/NimCap.Screenshot.exe", StringComparison.OrdinalIgnoreCase));
        var prefix = portableExecutable.Entry is null ? "" : "Program/";
        if (prefix.Length == 0)
        {
            var executables = entries.Where(x => x.Name.Equals("NimCap.Screenshot.exe", StringComparison.OrdinalIgnoreCase) ||
                x.Name.EndsWith("/NimCap.Screenshot.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (executables.Length != 1) throw new InvalidDataException("The package must contain one NimCap.Screenshot.exe.");
            prefix = executables[0].Name[..^"NimCap.Screenshot.exe".Length];
        }
        // Files next to Program/ are distribution metadata, never user data or program files.
        var rootExtras = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NimCap.Screenshot.exe", "portable", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "Data/.gitkeep"
        };
        var files = new List<string>();
        foreach (var (entry, name) in entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (prefix.Length == 0 || !rootExtras.Contains(name))
                    throw new InvalidDataException("Files outside the application directory.");
                continue;
            }
            var relative = name[prefix.Length..];
            ValidateRelativePath(relative);
            if (relative.Split('/').Any(part => ProtectedNames.Contains(part) || part.StartsWith('.') ||
                part.StartsWith("settings.json", StringComparison.OrdinalIgnoreCase)) ||
                Path.GetExtension(relative).ToLowerInvariant() is ".onnx" or ".gguf" or ".traineddata" or ".safetensors" or ".pth" or ".pfx" or ".key")
                throw new InvalidDataException("Update package contains user data or model files.");
            var destination = Path.Combine(stage, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = File.Create(destination);
            var buffer = new byte[81920];
            int count;
            long written = 0;
            while ((count = input.Read(buffer)) > 0)
            {
                cancellation.ThrowIfCancellationRequested();
                written += count;
                if (written > entry.Length) throw new InvalidDataException("Invalid expanded size.");
                output.Write(buffer, 0, count);
            }
            if (written != entry.Length) throw new InvalidDataException("Truncated update file.");
            files.Add(relative);
        }
        foreach (var required in new[] { "NimCap.Screenshot.exe", "NimCap.Screenshot.dll", "NimCap.Screenshot.deps.json", "NimCap.Screenshot.runtimeconfig.json", "coreclr.dll", "hostfxr.dll" })
            if (!files.Contains(required, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Incomplete self-contained update package: " + required);
        return new PreparedAppUpdate(directory, stage, files.ToArray());
    }

    private static void ValidateRelativePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Split('/').Any(part =>
            part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
            part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            Regex.IsMatch(part, @"\A(?:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe update path.");
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, Uri uri, CancellationToken cancellation)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length > 0 ||
                uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Untrusted update download host.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("NimCap-Screenshot-Updater/1.0");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                response.Dispose();
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new HttpRequestException("Too many update redirects.");
    }

    private sealed record ProgressCallback(Action<long> Action) : IProgress<long>
    { public void Report(long value) => Action(value); }

    private static async Task CopyBounded(Stream input, Stream output, long limit, IProgress<long>? progress, CancellationToken cancellation)
    {
        var buffer = new byte[81920];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("Update download exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
            progress?.Report(total);
        }
    }

    internal static async Task StartUpdaterAsync(PreparedAppUpdate update, string target, CancellationToken cancellation)
    {
        target = Path.GetFullPath(target);
        // Check write access before closing the application. Elevation is never requested automatically.
        var probe = Path.Combine(target, ".nimcap-screenshot-update-probe-" + Guid.NewGuid().ToString("N"));
        using (File.Create(probe)) { }
        File.Delete(probe);
        var handoff = Path.Combine(update.Directory, "handoff-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(handoff);
        var plan = Path.Combine(handoff, "plan.json");
        // Portable packages restart the launcher next to Program/ so portable mode stays active.
        var restart = Path.Combine(target, "NimCap.Screenshot.exe");
        var packageRoot = Directory.GetParent(target)?.FullName;
        if (packageRoot is not null && File.Exists(Path.Combine(packageRoot, "NimCap.Screenshot.exe")))
            restart = Path.Combine(packageRoot, "NimCap.Screenshot.exe");
        using var parent = Process.GetCurrentProcess();
        var files = update.Files.Select(relative => new
        {
            Relative = relative,
            Sha256 = FileHash(Path.Combine(update.Stage, relative))
        }).ToArray();
        await File.WriteAllTextAsync(plan, JsonSerializer.Serialize(new
        {
            Target = target,
            Restart = restart,
            update.Stage, Files = files, ParentId = parent.Id,
            ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
            Backup = Path.Combine(handoff, "backup"),
            Ready = Path.Combine(handoff, "ready"),
            Abort = Path.Combine(handoff, "abort"),
            Result = Path.Combine(handoff, "result.json")
        }), cancellation).ConfigureAwait(false);
        var script = Path.Combine(handoff, "install.ps1");
        using (var resource = typeof(AppUpdateInstaller).Assembly.GetManifestResourceStream("NimCap.Screenshot.AppUpdate.ps1")
            ?? throw new InvalidOperationException("Missing update helper."))
        await using (var output = File.Create(script)) await resource.CopyToAsync(output, cancellation).ConfigureAwait(false);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = update.Directory
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-Plan", plan }) start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new InvalidOperationException("Could not start update helper.");
        try
        {
            for (var i = 0; i < 600; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(handoff, "ready"))) return;
                if (helper.HasExited) throw new InvalidOperationException("Update helper failed; see " + Path.Combine(handoff, "result.json"));
                await Task.Delay(100, cancellation).ConfigureAwait(false);
            }
            throw new TimeoutException("Update helper did not become ready.");
        }
        catch
        {
            File.WriteAllText(Path.Combine(handoff, "abort"), "cancelled");
            // Until readiness is returned the parent is still alive, so no target files can have changed.
            try { if (!helper.HasExited) { helper.Kill(entireProcessTree: true); await helper.WaitForExitAsync().ConfigureAwait(false); } }
            catch (InvalidOperationException) { }
            throw;
        }
    }

    private static string FileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void Cleanup(string directory)
    {
        try { System.IO.Directory.Delete(directory, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
