using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Providers.HuggingFace;

/// <summary>
/// Hugging Face Provider Adapter — recurring daily free open-source 3D provider.
/// Connects to ZeroGPU Spaces (Hunyuan3D-2.0, TRELLIS.2) via the Gradio REST/Client API.
///
/// Gradio pattern:
///   POST  <space>/api/predict/      -> submit, returns event_id
///   GET   <space>/api/predict/{id}  -> poll result (file URLs served at <space>/file/<path>)
/// </summary>
public class HuggingFaceProvider : BaseProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    public string SpaceUrl { get; set; } = string.Empty; // e.g. https://xxx.hf.space
    public string Model { get; set; } = "hunyuan3d";     // hunyuan3d | trellis2

    public HuggingFaceProvider(string providerId, string name, string apiKey = "") : base(providerId, name, apiKey) { }

    public override async Task<ProviderKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderKeyValidationResult { Valid = false, Error = "HF_TOKEN is required." };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://huggingface.co/api/whoami-v2");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return new ProviderKeyValidationResult
            {
                Valid = true,
                Balance = new { username = root.TryGetProperty("username", out var u) ? u.GetString() : null },
                Message = "HF token valid.",
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
            throw new InvalidOperationException("HF_TOKEN not configured. Set it in Settings.");
        if (string.IsNullOrWhiteSpace(SpaceUrl))
            throw new InvalidOperationException("Hugging Face Space URL not configured (Settings).");

        var endpoint = $"{SpaceUrl.TrimEnd('/')}/api/predict/";
        var inputs = new JsonArray { imagePath ?? (JsonNode?)null, prompt,
            new JsonObject { ["topology"] = NormalizeTopology(options.Topology),
                             ["polycount"] = NormalizePolycount(options.Polycount.ToString()),
                             ["format"] = NormalizeFormat(options.Format),
                             ["model"] = Model } };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(new JsonObject { ["data"] = inputs }.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);

        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string eventId = root.TryGetProperty("event_id", out var e) ? e.GetString() ?? options.JobId : options.JobId;

        TrackJob(options.JobId, new ProviderJobStatus
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = ProviderId,
            Result = JsonSerializer.SerializeToNode(new { eventId, spaceUrl = SpaceUrl, format = options.Format }),
        });

        return new ProviderSubmitResult
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = ProviderId,
            ExternalJobId = eventId,
        };
    }

    public override async Task<ProviderJobStatus> PollJobStatusAsync(string jobId)
    {
        var job = GetJob(jobId);
        string? eventId = ExtractEventId(job);
        string spaceUrl = ExtractSpaceUrl(job) ?? SpaceUrl;
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(spaceUrl))
            throw new InvalidOperationException($"No Hugging Face event for job {jobId}.");

        var endpoint = $"{spaceUrl.TrimEnd('/')}/api/predict/{eventId}";
        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool success = root.TryGetProperty("success", out var succ) && succ.GetBoolean();
        var output = root.TryGetProperty("data", out var data) ? data : default;

        string mapped;
        int progress;
        JsonElement? result = null;
        if (success && output.HasValue && output.Value.ValueKind == JsonValueKind.Array && output.Value.GetArrayLength() > 0)
        {
            mapped = "completed";
            progress = 100;
            result = output.Value[0];
        }
        else if (!success)
        {
            mapped = "failed";
            progress = 100;
        }
        else
        {
            mapped = "running";
            progress = root.TryGetProperty("progress", out var pr) ? pr.GetInt32() : 40;
        }

        var tracked = new ProviderJobStatus
        {
            JobId = jobId,
            Status = mapped,
            Progress = progress,
            Provider = ProviderId,
            Result = result.HasValue ? JsonSerializer.SerializeToNode(result.Value) : null,
            Error = root.TryGetProperty("error", out var err) ? err.GetString() : null,
        };
        TrackJob(jobId, tracked);
        return tracked;
    }

    public override async Task<ProviderDownloadResult> DownloadToLocalFolderAsync(string jobId, string outputDir)
    {
        var job = GetJob(jobId) ?? throw new InvalidOperationException($"Unknown HF job {jobId}");
        var resultNode = job.Result as JsonNode?;
        string format = resultNode?["format"]?.GetValue<string>() ?? "glb";
        format = NormalizeFormat(format);

        string? fileUrl = null;
        if (resultNode is not null)
        {
            fileUrl = resultNode["url"]?.GetValue<string>() ?? resultNode["file_url"]?.GetValue<string>() ?? resultNode["path"]?.GetValue<string>();
        }

        if (string.IsNullOrEmpty(fileUrl))
            throw new InvalidOperationException("No output file from Hugging Face job " + jobId);

        // Gradio file URLs are relative to the space.
        string spaceUrl = ExtractSpaceUrl(job) ?? SpaceUrl;
        if (fileUrl.StartsWith("/"))
            fileUrl = $"{spaceUrl.TrimEnd('/')}/file/{fileUrl.TrimStart('/')}";

        var outPath = MakeOutputPath(outputDir, Path.GetFileName(fileUrl) ?? $"hf-{jobId}", format);

        using var resp = await _http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
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
            Mime = "model/gltf-binary",
            Provider = ProviderId,
        };
    }

    private static string? ExtractEventId(ProviderJobStatus? job)
    {
        if (job?.Result is JsonNode node && node["eventId"] is { } v) return v.GetValue<string>();
        return null;
    }
    private static string? ExtractSpaceUrl(ProviderJobStatus? job)
    {
        if (job?.Result is JsonNode node && node["spaceUrl"] is { } v) return v.GetValue<string>();
        return null;
    }
}
