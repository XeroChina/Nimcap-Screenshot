using System.Net;
using System.Net.Http;
using System.Text.Json;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.RegressionChecks;

internal static partial class Program
{
#if UPDATE_CHECKS_ONLY
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--update-parent")) { await Task.Delay(5000); return 0; }
        if (Environment.GetEnvironmentVariable("NIMCAP_SCREENSHOT_UPDATE_RESTART_MARKER") is { } marker)
        { File.WriteAllText(marker, "restarted"); return 0; }
        try { await AppUpdateChecks(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
#endif
    private static async Task AppUpdateChecks()
    {
        static string Release(string tag = "v1.0.9", bool prerelease = false,
            string package = "nimcap-screenshot-v1.0.9-win-x64.zip", string host = "github.com") => JsonSerializer.Serialize(new
        {
            tag_name = tag, draft = false, prerelease, body = "Release notes\nSecond line",
            html_url = $"https://{host}/{AppUpdateService.RepositorySlug}/releases/tag/{tag}",
            assets = new[]
            {
                new { name = package, browser_download_url = $"https://{host}/{AppUpdateService.RepositorySlug}/releases/download/{tag}/{package}", digest = "sha256:" + new string('a', 64) },
                new { name = "SHA256SUMS.txt", browser_download_url = $"https://{host}/{AppUpdateService.RepositorySlug}/releases/download/{tag}/SHA256SUMS.txt", digest = "" }
            }
        });
        static HttpClient Client(string json, HttpStatusCode status = HttpStatusCode.OK) =>
            new(new AppUpdateHttpHandler(json, status));
        using (var client = Client(Release()))
        {
            var release = await AppUpdateService.CheckAsync(CancellationToken.None, client);
            Require(release is not null && release.IsNewerThan(new Version(1, 0, 8)), "New stable release not detected.");
            Require(!release!.IsNewerThan(new Version(1, 0, 9)) && !release.IsNewerThan(new Version(1, 0, 9, 0)) &&
                !release.IsNewerThan(new Version(1, 0, 10)), "Equal versions or newer local versions must not prompt for updates.");
            Require(release.Download?.AbsoluteUri.EndsWith("win-x64.zip") == true && release.Notes.Contains('\n'), "Missing package or multiline release notes.");
            Require(release.Sha256 == new string('a', 64) && release.Checksums?.AbsolutePath.EndsWith("SHA256SUMS.txt") == true,
                "GitHub asset digest or checksum fallback missing.");
        }
        using (var client = Client(Release(package: "Source.zip")))
            Require((await AppUpdateService.CheckAsync(CancellationToken.None, client))?.Download is null, "Source archive offered as a program update.");
        using (var client = Client(Release(prerelease: true)))
            Require(await AppUpdateService.CheckAsync(CancellationToken.None, client) is null, "Prerelease offered as a stable update.");
        using (var client = Client("{}", HttpStatusCode.NotFound))
            Require(await AppUpdateService.CheckAsync(CancellationToken.None, client) is null, "Empty release repository not handled.");
        foreach (var json in new[] { Release(tag: "v1.1.0-rc1"), Release(host: "example.org"), "not json" })
        {
            using var client = Client(json);
            var rejected = false;
            try { await AppUpdateService.CheckAsync(CancellationToken.None, client); }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or JsonException) { rejected = true; }
            Require(rejected, "Invalid release metadata accepted.");
        }
        using (var client = Client("{}", HttpStatusCode.Forbidden))
        {
            var failed = false;
            try { await AppUpdateService.CheckAsync(CancellationToken.None, client); }
            catch (HttpRequestException) { failed = true; }
            Require(failed, "Rate limiting must be reported as a failure, not as up to date.");
        }
        using (var client = Client(Release()))
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var cancelled = false;
            try { await AppUpdateService.CheckAsync(cancellation.Token, client); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled, "Closing settings must cancel the update check.");
        }
        await AppUpdateInstallerChecks();
        Console.WriteLine("PASS: stable update detection, normalized versions, full package selection, release notes, invalid metadata, rate limits and cancellation.");
    }

    private sealed class AppUpdateHttpHandler(string json, HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Require(request.RequestUri?.AbsoluteUri == $"https://api.github.com/repos/{AppUpdateService.RepositorySlug}/releases/latest", "Wrong update endpoint.");
            Require(request.Headers.UserAgent.Count > 0 && request.Headers.Accept.Any(), "GitHub API headers missing.");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
