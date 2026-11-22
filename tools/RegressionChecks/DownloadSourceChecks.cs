using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using NimCap.Screenshot.Models;
using NimCap.Screenshot.Services;

namespace NimCap.Screenshot.RegressionChecks;

internal static partial class Program
{
    private static async Task DownloadSourceChecks()
    {
        const string domestic = ModelDownloadSource.Domestic;
        var sources = new[] { OcrModelDownloadService.Source, SpecialistModelDownloadService.Table.Url,
            $"https://github.com/ggml-org/llama.cpp/releases/download/{LocalTranslationSetupService.Version}/llama.zip",
            $"https://huggingface.co/Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/{LocalTranslationSetupService.ModelRevision}/{LocalTranslationSetupService.ModelName}" }
            .Concat(TesseractModelDownloadService.Models.Select(x => x.Url))
            .Concat(SpecialistModelDownloadService.Formula.Select(x => x.Url))
            .Concat(CutoutModels.All.Select(x => x.Source)).ToArray();
        foreach (var source in sources)
        {
            Require(ModelDownloadSource.Resolve(source, ModelDownloadSource.Official) == source, "Official URL was changed.");
            Require(ModelDownloadSource.Resolve(source, "unknown") == source, "Unknown source should retain the official URL.");
            var resolved = ModelDownloadSource.Resolve(source, domestic);
            Require(ModelDownloadSource.Allowed(new Uri(resolved), domestic), "Mirror URL was rejected: " + resolved);
            Require(resolved != source || source == SpecialistModelDownloadService.Table.Url, "Missing mirror: " + source);
        }
        const string pinned = "https://huggingface.co/test/model/resolve/0123456789/model.gguf?download=true";
        var mirror = ModelDownloadSource.Resolve(pinned, domestic);
        Require(mirror == "https://hf-mirror.com/test/model/resolve/0123456789/model.gguf?download=true", "Mirror discarded the pinned revision or query.");
        Require(ModelDownloadSource.Resolve(OcrModelDownloadService.Source, domestic).EndsWith(new Uri(OcrModelDownloadService.Source).AbsolutePath),
            "PyPI mirror changed the pinned package path.");
        foreach (var invalid in new[] { "http://hf-mirror.com/a", "https://hf-mirror.com:444/a", "https://user@hf-mirror.com/a",
            "https://hf-mirror.com.example.com/a", "https://evil-hf.co/a", "https://example.com/a" })
            Require(!ModelDownloadSource.Allowed(new Uri(invalid), domestic), "Untrusted mirror URL was accepted: " + invalid);
        Require(!ModelDownloadSource.Allowed(new Uri(mirror)), "Official mode accepted a mirror redirect.");
        // ModelScope serves LFS objects from a CDN subdomain after a 302.
        foreach (var sourceKind in new[] { ModelDownloadSource.Official, domestic })
            Require(ModelDownloadSource.Allowed(new Uri("https://cdn-lfs-cn-1.modelscope.cn/prod/lfs-objects/aa/bb/model.onnx"), sourceKind),
                "ModelScope LFS CDN redirect was rejected: " + sourceKind);

        var payload = new byte[] { 1, 3, 5, 7, 9 };
        var target = Path.Combine(Root, "switch-source");
        File.WriteAllBytes(target + ".part", payload[..2]);
        var handler = new Handler((request, call) =>
        {
            Require(request.Headers.Range?.Ranges.Single().From == 2, "Switching source lost the resume offset.");
            if (call == 1)
            {
                Require(request.RequestUri!.Host == "hf-mirror.com", "Mirror was not requested.");
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://us.aws.cdn.hf.co/model") } };
            }
            return Reply(payload, 2, partial: true);
        });
        using (var client = new HttpClient(handler))
            await ResumableModelDownload.Download(pinned, target, Convert.ToHexString(SHA256.HashData(payload)), payload.Length,
                new Inline<string>(_ => { }), default, httpClient: client, sourceKind: domestic);
        Require(File.ReadAllBytes(target).SequenceEqual(payload) && handler.Calls == 2, "Mirror resume or checksum verification failed.");

        using (var client = new HttpClient(new Handler((request, _) =>
        {
            Require(request.RequestUri!.Host == "gh-proxy.com" && request.RequestUri.AbsolutePath.EndsWith("/v0.0.0/tokenizer.json"),
                "Formula API asset was not mapped to its release file.");
            return Reply(payload);
        })))
        using (var response = await ModelDownloadSource.Get(client, SpecialistModelDownloadService.Formula[^1].Url, domestic, default))
            Require(response.IsSuccessStatusCode, "Formula mirror failed.");

        using (var client = new HttpClient(new Handler((request, call) =>
        {
            if (call == 1)
                return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://cdn-lfs-cn-1.modelscope.cn/prod/lfs-objects/aa/bb/table.onnx") } };
            Require(request.RequestUri!.Host == "cdn-lfs-cn-1.modelscope.cn", "ModelScope CDN was not requested.");
            return Reply(payload);
        })))
        using (var response = await ModelDownloadSource.Get(client, SpecialistModelDownloadService.Table.Url, domestic, default))
            Require(response.IsSuccessStatusCode, "ModelScope LFS redirect failed.");

        var untrusted = new Handler((_, _) => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/file") } });
        using (var client = new HttpClient(untrusted))
        {
            try
            {
                using var response = await ModelDownloadSource.Get(client, OcrModelDownloadService.Source, domestic, default);
                throw new Exception("Shared downloader accepted an untrusted redirect.");
            }
            catch (InvalidDataException) { Require(untrusted.Calls == 1, "Untrusted host received a request."); }
        }
        Console.WriteLine("PASS: official/mirror model sources, pinned URLs, trusted redirects, formula asset mapping, source switching and verified resume.");
    }
}
