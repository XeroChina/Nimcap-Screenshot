using System.Net.Http;
using System.Net.Http.Headers;
using L = NimCap.Screenshot.Localization.UiText;

namespace NimCap.Screenshot.Services;

/// <summary>Changes transport only; callers still verify the pinned upstream bytes.</summary>
internal static class ModelDownloadSource
{
    internal const string Official = "Official";
    internal const string Domestic = "Domestic";

    internal static string Resolve(string source, string sourceKind)
    {
        if (sourceKind != Domestic) return source;
        var uri = new Uri(source);
        if (uri.Host == "huggingface.co")
            return new UriBuilder(uri) { Host = "hf-mirror.com" }.Uri.AbsoluteUri;
        if (uri.Host == "files.pythonhosted.org")
            return new UriBuilder(uri) { Host = "pypi.tuna.tsinghua.edu.cn" }.Uri.AbsoluteUri;
        if (uri.Host == "api.github.com")
        {
            var asset = SpecialistModelDownloadService.Formula.Single(x => x.Url == source);
            source = "https://github.com/RapidAI/RapidLaTeXOCR/releases/download/v0.0.0/" + asset.Name;
        }
        if (uri.Host is "github.com" or "raw.githubusercontent.com" or "api.github.com")
            return "https://gh-proxy.com/" + source;
        // The table model is already distributed by ModelScope in China.
        return source;
    }

    internal static bool Allowed(Uri uri, string sourceKind = Official) =>
        uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.AbsoluteUri == NimCap.Screenshot.Models.WatermarkModels.Detector.Source ||
         uri.Host is "github.com" or "api.github.com" or "raw.githubusercontent.com" or
            "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or
            "files.pythonhosted.org" or "www.modelscope.cn" or "modelscope.cn" or
            "huggingface.co" or "hf.co" ||
         uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase) ||
         // ModelScope serves LFS objects from a CDN subdomain, e.g. cdn-lfs-cn-1.modelscope.cn.
         uri.Host.EndsWith(".modelscope.cn", StringComparison.OrdinalIgnoreCase) ||
         sourceKind == Domestic && (uri.Host is "hf-mirror.com" or "gh-proxy.com" or
             "pypi.tuna.tsinghua.edu.cn" or "mirrors.tuna.tsinghua.edu.cn"));

    // Clients must disable automatic redirects so every destination is checked.
    internal static async Task<HttpResponseMessage> Get(HttpClient client, string source, string sourceKind, CancellationToken token)
    {
        var uri = new Uri(Resolve(source, sourceKind));
        for (var redirects = 0; redirects <= 8; redirects++)
        {
            if (!Allowed(uri, sourceKind))
                throw new InvalidDataException(L.Get("ModelSourceRedirectedToAnUntrustedAddress"));
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("NimCap-Screenshot/1.0");
            if (uri.Host == "api.github.com")
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
            {
                response.Dispose();
                uri = new Uri(uri, next);
                continue;
            }
            return response;
        }
        throw new InvalidDataException(L.Get("TooManyRedirectsWhileDownloadingTheModel"));
    }
}
