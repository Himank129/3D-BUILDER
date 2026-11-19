using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Providers.Tripo;

/// <summary>
/// Tripo AI Provider Adapter — primary free-tier direct 3D mesh provider.
/// Endpoints (verify against current Tripo OpenAPI):
///   POST   /v2/openapi/generation/jobs        -> submit text/image-to-3D
///   GET    /v2/openapi/generation/jobs/:id    -> poll status
///   GET    /v2/openapi/user/balance           -> balance check ("Test Key" button)
/// Post-processing: retopology (quad) + smart_uv before download. Exports .glb/.fbx.
/// </summary>
public class TripoProvider : BaseProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private const string BaseUrl = "https://api.tripo3d.ai/v2/openapi";

    public TripoProvider(string providerId, string name, string apiKey = "") : base(providerId, name, apiKey) { }

    public override async Task<ProviderKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderKeyValidationResult { Valid = false, Error = "Tripo API key is required." };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/user/balance");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            object? balance = root.TryGetProperty("data", out var d) ? (object?)d : (object?)root;
            return new ProviderKeyValidationResult
            {
                Valid = true,
                Balance = balance,
                Message = "Tripo key valid.",
            };
        }
        catch (Exception ex)
        {
            return new ProviderKeyValidationResult { Valid = false, Error = ex.Message };
        }
    }

    public override async Task<ProviderSubmitResult> SubmitGenerationJobAsync(string prompt, string? imagePath, GenerationOptions options)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("Tripo API key not configured. Set it in Settings.");

        var payload = new JsonObject
        {
            ["prompt"] = prompt,
            ["model_version"] = "v2",
            ["topology"] = NormalizeTopology(options.Topology),
            ["target_polycount"] = NormalizePolycount(options.Polycount.ToString()),
            ["output_format"] = NormalizeFormat(options.Format),
            ["process"] = new JsonObject { ["retopology"] = true, ["smart_uv"] = true },
        };
        if (!string.IsNullOrEmpty(imagePath))
            payload["image"] = imagePath;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/generation/jobs")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;

        string externalId = options.JobId;
        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("job_id", out var jid)) externalId = jid.GetString() ?? options.JobId;
            else if (data.TryGetProperty("id", out var id)) externalId = id.GetString() ?? options.JobId;
        }
        else if (root.TryGetProperty("job_id", out var jid2))
        {
            externalId = jid2.GetString() ?? options.JobId;
        }

        TrackJob(options.JobId, new ProviderJobStatus
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = ProviderId,
            Result = JsonSerializer.SerializeToNode(new { externalJobId = externalId, format = options.Format }),
        });

        return new ProviderSubmitResult
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = ProviderId,
            ExternalJobId = externalId,
            Raw = root,
        };
    }

    public override async Task<ProviderJobStatus> PollJobStatusAsync(string jobId)
    {
        var job = GetJob(jobId);
        string externalId = ExtractExternalId(job) ?? jobId;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/generation/jobs/{externalId}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var respJson = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;
        var jdata = root.TryGetProperty("data", out var d) ? d : root;

        string statusStr = jdata.TryGetProperty("status", out var s) ? s.GetString() ?? "queued" : "queued";
        var mapped = MapStatus(statusStr);
        int progress = jdata.TryGetProperty("progress", out var p) ? p.GetInt32() : mapped == "running" ? 50 : (mapped == "completed" ? 100 : 0);

        var tracked = new ProviderJobStatus
        {
            JobId = jobId,
            Status = mapped,
            Progress = progress,
            Provider = ProviderId,
            Result = jdata,
            Error = jdata.TryGetProperty("error", out var err) ? err.GetString() : null,
        };
        TrackJob(jobId, tracked);
        return tracked;
    }

    public override async Task<ProviderDownloadResult> DownloadToLocalFolderAsync(string jobId, string outputDir)
    {
        var job = GetJob(jobId) ?? throw new InvalidOperationException($"Unknown Tripo job {jobId}");
        var resultNode = job.Result as JsonNode?;
        string format = resultNode?["format"]?.GetValue<string>() ?? "glb";
        format = NormalizeFormat(format);

        string? fileUrl = null;
        string? fileName = null;
        if (resultNode is not null)
        {
            if (resultNode.TryGetProperty("file_url", out var fu)) fileUrl = fu.GetValue<string>();
            else if (resultNode.TryGetProperty("url", out var u)) fileUrl = u.GetValue<string>();
            else if (resultNode.TryGetProperty("model_url", out var mu)) fileUrl = mu.GetValue<string>();
            if (resultNode.TryGetProperty("file_name", out var fn)) fileName = fn.GetValue<string>();
        }

        // If the job payload didn't embed a file url, fetch the /models sub-endpoint.
        if (string.IsNullOrEmpty(fileUrl))
        {
            var extJobId = ExtractExternalId(job) ?? jobId;
            using var mreq = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/generation/jobs/{extJobId}/models");
            mreq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
            using var mresp = await _http.SendAsync(mreq);
            if (mresp.IsSuccessStatusCode)
            {
                var mjson = await mresp.Content.ReadAsStringAsync();
                using var mdoc = JsonDocument.Parse(mjson);
                var models = mdoc.RootElement;
                JsonElement first = default;
                bool found = false;
                if (models.TryGetProperty("data", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                {
                    first = arr[0]; found = true;
                }
                else if (models.ValueKind == JsonValueKind.Array && models.GetArrayLength() > 0)
                {
                    first = models[0]; found = true;
                }
                if (found)
                {
                    if (first.TryGetProperty("file_url", out var fu)) fileUrl = fu.GetString();
                    else if (first.TryGetProperty("url", out var u)) fileUrl = u.GetString();
                    else if (first.TryGetProperty("download_url", out var du)) fileUrl = du.GetString();
                    if (first.TryGetProperty("file_name", out var fn)) fileName = fn.GetString();
                }
            }
        }

        if (string.IsNullOrEmpty(fileUrl))
            throw new InvalidOperationException("No downloadable model file found for Tripo job " + jobId);

        var outPath = MakeOutputPath(outputDir, fileName ?? $"tripo-{jobId}", format);

        using var resp = await _http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(outPath);
        await resp.Content.CopyToAsync(fs);

        var info = new FileInfo(outPath);
        TrackJob(jobId, new ProviderJobStatus
        {
            JobId = jobId,
            Status = "downloaded",
            Progress = 100,
            Provider = ProviderId,
            Result = JsonSerializer.SerializeToNode(new { filePath = outPath, fileName = info.Name, fileSize = info.Length }),
        });

        return new ProviderDownloadResult
        {
            FilePath = outPath,
            FileName = info.Name,
            FileSize = info.Length,
            Mime = format == "fbx" ? "application/octet-stream" : "model/gltf-binary",
            Provider = ProviderId,
        };
    }

    private static string? ExtractExternalId(ProviderJobStatus? job)
    {
        if (job?.Result is JsonNode node && node["externalJobId"] is { } v)
            return v.GetValue<string>();
        return null;
    }

    private static string MapStatus(string s) => s.ToLowerInvariant() switch
    {
        "success" or "completed" or "done" => "completed",
        "failed" or "error" or "cancelled" => "failed",
        "running" or "processing" => "running",
        _ => "queued"
    };
}
