using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace NimCap.Screenshot.Services;

internal sealed record AppRelease(Version Version, string Tag, string Notes, Uri Page, Uri? Download,
    string? Sha256 = null, Uri? Checksums = null)
{
    public bool IsNewerThan(Version current) => Version > AppUpdateService.Normalize(current);
}

internal static class AppUpdateService
{
    internal const string RepositorySlug = "XeroChina/nimcap-screenshot";
    internal const string ReleasesUrl = "https://github.com/" + RepositorySlug + "/releases";
    private const string ApiUrl = "https://api.github.com/repos/" + RepositorySlug + "/releases/latest";

    internal static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    internal static async Task<AppRelease?> CheckAsync(CancellationToken cancellation, HttpClient? client = null)
    {
        using var ownedClient = client is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
                { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 }
            : null;
        client ??= ownedClient!;
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
        request.Headers.UserAgent.ParseAdd("NimCap-Screenshot/" + typeof(AppUpdateService).Assembly.GetName().Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, cancellation).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation).ConfigureAwait(false));
        var release = json.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new InvalidDataException("Invalid release version.");
        var page = TrustedUrl(release.GetProperty("html_url").GetString(), "/" + RepositorySlug + "/releases/tag/")
            ?? throw new InvalidDataException("Invalid release URL.");
        Uri? download = null;
        Uri? checksums = null;
        string? sha256 = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            // Only use the full Windows x64 distribution, never GitHub's source archive.
            var expectedName = $"nimcap-screenshot-{tag}-win-x64.zip";
            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.GetProperty("name").GetString() == "SHA256SUMS.txt")
                    checksums = TrustedUrl(asset.GetProperty("browser_download_url").GetString(), "/" + RepositorySlug + "/releases/download/");
                if (!string.Equals(asset.GetProperty("name").GetString(), expectedName, StringComparison.OrdinalIgnoreCase)) continue;
                download = TrustedUrl(asset.GetProperty("browser_download_url").GetString(), "/" + RepositorySlug + "/releases/download/");
                if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } value &&
                    value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                    value.Length == 71 && value[7..].All(Uri.IsHexDigit)) sha256 = value[7..];
            }
        }
        var notes = release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new AppRelease(Normalize(version), tag, notes, page, download, sha256, checksums);
    }

    private static Uri? TrustedUrl(string? value, string prefix) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)
            ? uri : null;
}
