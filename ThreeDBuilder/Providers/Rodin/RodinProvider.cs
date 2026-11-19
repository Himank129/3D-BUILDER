using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Providers.Rodin;

/// <summary>
/// Rodin / Hyper3D Provider Adapter — paid Pro high-precision 3D provider.
/// Endpoints (verify against Rodin/Hyper3D API docs):
///   POST   /v1/generation           -> submit text/image-to-3D (job id)
///   GET    /v1/generation/{id}      -> poll status + model url
/// Native quad-mesh output with PBR material maps (.glb/.fbx).
/// </summary>
public class RodinProvider : BaseProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    public string BaseUrl { get; set; } = "https://api.hyper3d.ai/v1";

    public RodinProvider(string providerId, string name, string apiKey = "") : base(providerId, name, apiKey) { }

    public override async Task<ProviderKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderKeyValidationResult { Valid = false, Error = "RODIN_API_KEY is required." };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl.TrimEnd('/')}/generation");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req);
            bool valid = resp.IsSuccessStatusCode || resp.StatusCode == System.Net.HttpStatusCode.MethodNotAllowed;
            return new ProviderKeyValidationResult
            {
                Valid = valid,
                Message = valid ? "Rodin key accepted." : "Rodin key rejected.",
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
            throw new InvalidOperationException("RODIN_API_KEY not configured. Set it in Settings.");

        var payload = new JsonObject
        {
            ["prompt"] = prompt,
            ["topology"] = "quad",                 // Rodin native quad-mesh
            ["target_polycount"] = NormalizePolycount(options.Polycount.ToString()),
            ["output_format"] = NormalizeFormat(options.Format),
            ["pbr_maps"] = true,                   // native PBR material maps
        };
        if (!string.IsNullOrEmpty(imagePath))
            payload["image_url"] = imagePath;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl.TrimEnd('/')}/generation")
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
            : root.TryGetProperty("job_id", out var jid) ? jid.GetString() ?? options.JobId
            : root.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? options.JobId
            : options.JobId;

        TrackJob(options.JobId, new ProviderJobStatus
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = ProviderId,
            Result = JsonSerializer.SerializeToNode(new { jobId, format = options.Format }),
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
        string? taskId = ExtractJobId(job) ?? jobId;

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl.TrimEnd('/')}/generation/{taskId}");
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
        var job = GetJob(jobId) ?? throw new InvalidOperationException($"Unknown Rodin job {jobId}");
        var resultNode = job.Result as JsonNode?;
        string format = NormalizeFormat(resultNode?["format"]?.GetValue<string>() ?? "glb");

        string? modelUrl = resultNode?["model_url"]?.GetValue<string>() ?? resultNode?["url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(modelUrl))
        {
            var res = resultNode?["result"];
            modelUrl = res?["model_url"]?.GetValue<string>() ?? res?["url"]?.GetValue<string>();
        }

        if (string.IsNullOrEmpty(modelUrl))
            throw new InvalidOperationException("No model URL from Rodin job " + jobId);

        var outPath = MakeOutputPath(outputDir, $"rodin-{jobId}", format);

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

    private static string? ExtractJobId(ProviderJobStatus? job)
    {
        if (job?.Result is JsonNode node && node["jobId"] is { } v) return v.GetValue<string>();
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
