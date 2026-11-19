using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Providers.Meshy;

/// <summary>
/// Meshy AI Provider Adapter — paid Pro direct 3D mesh provider.
/// Endpoints (verify against Meshy API docs, typically api.meshy.ai):
///   POST   /openapi/v2/text-to-3d   -> submit text-to-3D (job id)
///   POST   /openapi/v2/image-to-3d  -> submit image-to-3D
///   GET    /openapi/v2/tasks/{id}   -> poll status + model url
/// Pass topology: "quad" and target_polycount; export .glb/.fbx.
/// </summary>
public class MeshyProvider : BaseProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    public string BaseUrl { get; set; } = "https://api.meshy.ai/openapi/v2";

    public MeshyProvider(string providerId, string name, string apiKey = "") : base(providerId, name, apiKey) { }

    public override async Task<ProviderKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderKeyValidationResult { Valid = false, Error = "MESHY_API_KEY is required." };

        try
        {
            // Meshy has no public balance endpoint in v2; validate by probing the tasks list (lightweight).
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl.TrimEnd('/')}/text-to-3d");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req);
            bool valid = resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
            return new ProviderKeyValidationResult
            {
                Valid = valid,
                Message = valid ? "Meshy key accepted." : "Meshy key rejected.",
                Error = valid ? null : $"HTTP {(int)resp.StatusCode}",
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
            throw new InvalidOperationException("MESHY_API_KEY not configured. Set it in Settings.");

        string endpoint = string.IsNullOrEmpty(imagePath)
            ? $"{BaseUrl.TrimEnd('/')}/text-to-3d"
            : $"{BaseUrl.TrimEnd('/')}/image-to-3d";

        var payload = new JsonObject
        {
            ["prompt"] = prompt,
            ["topology"] = NormalizeTopology(options.Topology),   // "quad"
            ["target_polycount"] = NormalizePolycount(options.Polycount.ToString()),
        };
        if (!string.IsNullOrEmpty(imagePath))
            payload["image_url"] = imagePath;

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string jobId = root.TryGetProperty("id", out var id) ? id.GetString() ?? options.JobId
            : root.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? options.JobId
            : options.JobId;

        TrackJob(options.JobId, new ProviderJobStatus
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = ProviderId,
            Result = JsonSerializer.SerializeToNode(new { taskId = jobId, format = options.Format }),
        });

        return new ProviderSubmitResult
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = ProviderId,
            ExternalJobId = jobId,
        };
    }

    public override async Task<ProviderJobStatus> PollJobStatusAsync(string jobId)
    {
        var job = GetJob(jobId);
        string? taskId = ExtractTaskId(job) ?? jobId;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl.TrimEnd('/')}/text-to-3d/{taskId}");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string statusStr = root.TryGetProperty("status", out var s) ? s.GetString() ?? "queued" : "queued";
        var mapped = MapStatus(statusStr);
        int progress = root.TryGetProperty("progress", out var p) ? p.GetInt32() : mapped == "running" ? 50 : (mapped == "completed" ? 100 : 0);

        var tracked = new ProviderJobStatus
        {
            JobId = jobId,
            Status = mapped,
            Progress = progress,
            Provider = ProviderId,
            Result = root,
            Error = root.TryGetProperty("error", out var err) ? err.GetString() : null,
        };
        TrackJob(jobId, tracked);
        return tracked;
    }

    public override async Task<ProviderDownloadResult> DownloadToLocalFolderAsync(string jobId, string outputDir)
    {
        var job = GetJob(jobId) ?? throw new InvalidOperationException($"Unknown Meshy job {jobId}");
        var resultNode = job.Result as JsonNode?;
        string format = NormalizeFormat(resultNode?["format"]?.GetValue<string>() ?? "glb");

        string? modelUrl = resultNode?["model_url"]?.GetValue<string>() ?? resultNode?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(modelUrl))
        {
            // Status response may nest the result under "result".
            var res = resultNode?["result"];
            modelUrl = res?["model_url"]?.GetValue<string>() ?? res?["url"]?.GetValue<string>();
        }

        if (string.IsNullOrEmpty(modelUrl))
            throw new InvalidOperationException("No model URL from Meshy job " + jobId);

        var outPath = MakeOutputPath(outputDir, $"meshy-{jobId}", format);

        using var resp = await _http.GetAsync(modelUrl, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var fs = File.Create(outPath);
        await resp.Content.CopyToAsync(fs);

        var info = new FileInfo(outPath);
        TrackJob(jobId, new ProviderJobStatus { JobId = jobId, Status = "downloaded", Progress = 100, Provider = ProviderId });

        return new ProviderDownloadResult
        {
            FilePath = outPath,
            FileName = info.Name,
            FileSize = info.Length,
            Mime = format == "fbx" ? "application/octet-stream" : "model/gltf-binary",
            Provider = ProviderId,
        };
    }

    private static string? ExtractTaskId(ProviderJobStatus? job)
    {
        if (job?.Result is JsonNode node && node["taskId"] is { } v) return v.GetValue<string>();
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
