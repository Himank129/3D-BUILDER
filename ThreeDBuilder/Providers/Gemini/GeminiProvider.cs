using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Providers.Gemini;

/// <summary>
/// Google Gemini Provider Adapter — high-volume procedural Blender script generator.
/// Generates clean, modifier-based bpy Python scripts from text/image prompts using
/// Gemini Flash models. Saves .py scripts that build geometry in Blender.
/// </summary>
public class GeminiProvider : BaseProvider
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(90) };
    // Gemini v1beta generateContent endpoint (user sets the model, e.g. gemini-1.5-flash).
    public string Model { get; set; } = "gemini-1.5-flash";
    public string? RegionEndpoint { get; set; } // optional override

    public GeminiProvider(string providerId, string name, string apiKey = "") : base(providerId, name, apiKey) { }

    public override async Task<ProviderKeyValidationResult> ValidateKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ProviderKeyValidationResult { Valid = false, Error = "GEMINI_API_KEY is required." };

        try
        {
            var url = string.IsNullOrWhiteSpace(RegionEndpoint)
                ? $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={apiKey}"
                : RegionEndpoint;
            var body = new JsonObject
            {
                ["contents"] = new JsonArray { new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = "Reply with exactly: OK" } } } }
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            return new ProviderKeyValidationResult { Valid = true, Message = "Gemini key valid." };
        }
        catch (Exception ex)
        {
            return new ProviderKeyValidationResult { Valid = false, Error = ex.Message };
        }
    }

    public override async Task<ProviderSubmitResult> SubmitGenerationJobAsync(string prompt, string? imagePath, GenerationOptions options)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException("GEMINI_API_KEY not configured. Set it in Settings.");

        var scriptPrompt = BuildScriptPrompt(prompt, options);

        var contents = new JsonArray
        {
            new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = scriptPrompt } } }
        };
        if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
        {
            // Attach image inline (base64) — Gemini supports image input.
            var bytes = await File.ReadAllBytesAsync(imagePath);
            var b64 = Convert.ToBase64String(bytes);
            var ext = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
            var mime = ext switch { "png" => "image/png", "jpg" or "jpeg" => "image/jpeg", "webp" => "image/webp", _ => "application/octet-stream" };
            ((JsonObject)contents[0])["parts"] = new JsonArray
            {
                new JsonObject { ["inline_data"] = new JsonObject { ["mime_type"] = mime, ["data"] = b64 } },
                new JsonObject { ["text"] = scriptPrompt }
            };
        }

        var url = string.IsNullOrWhiteSpace(RegionEndpoint)
            ? $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent?key={ApiKey}"
            : RegionEndpoint;

        var body = new JsonObject
        {
            ["contents"] = contents,
            ["generationConfig"] = new JsonObject { ["temperature"] = 0.2, ["maxOutputTokens"] = 8192 }
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string scriptText = "";
        if (root.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
        {
            var first = cands[0];
            if (first.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
            {
                scriptText = parts[0].TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            }
        }

        TrackJob(options.JobId, new ProviderJobStatus
        {
            JobId = options.JobId,
            Status = "completed", // script generation is synchronous
            Progress = 100,
            Provider = ProviderId,
            Result = JsonSerializer.SerializeToNode(new { script = scriptText, format = options.Format }),
        });

        return new ProviderSubmitResult
        {
            JobId = options.JobId,
            Status = "completed",
            Progress = 100,
            Provider = ProviderId,
        };
    }

    public override Task<ProviderJobStatus> PollJobStatusAsync(string jobId)
    {
        var job = GetJob(jobId) ?? new ProviderJobStatus { JobId = jobId, Status = "completed", Progress = 100, Provider = ProviderId };
        return Task.FromResult(job);
    }

    public override async Task<ProviderDownloadResult> DownloadToLocalFolderAsync(string jobId, string outputDir)
    {
        var job = GetJob(jobId) ?? throw new InvalidOperationException($"Unknown Gemini job {jobId}");
        var resultNode = job.Result as JsonNode?;
        string scriptText = resultNode?["script"]?.GetValue<string>() ?? "";
        string format = NormalizeFormat(resultNode?["format"]?.GetValue<string>() ?? "py");

        if (string.IsNullOrWhiteSpace(scriptText))
            throw new InvalidOperationException("No generated script for Gemini job " + jobId);

        var outPath = MakeOutputPath(outputDir, $"gemini-{jobId}", "py");
        await File.WriteAllTextAsync(outPath, scriptText);

        var info = new FileInfo(outPath);
        TrackJob(jobId, new ProviderJobStatus { JobId = jobId, Status = "downloaded", Progress = 100, Provider = ProviderId });

        return new ProviderDownloadResult
        {
            FilePath = outPath,
            FileName = info.Name,
            FileSize = info.Length,
            Mime = "text/x-python",
            PythonScriptPath = outPath,
            Provider = ProviderId,
        };
    }

    private static string BuildScriptPrompt(string prompt, GenerationOptions options)
    => $@"
You are an expert Blender Python (bpy) technical artist. Write a COMPLETE, standalone Blender Python script that builds clean, modifier-based 3D geometry from this prompt:

PROMPT: {prompt}

REQUIREMENTS:
- Use only bpy (Blender's Python API). No external libraries.
- Build with modifiers (Subdivision Surface, Mirror, Solidify, Bevel, Remesh) so the result is editable in Blender.
- Target topology: {NormalizeTopology(options.Topology)}.
- Target polycount approx: {NormalizePolycount(options.Polycount.ToString())}.
- Clear the default scene first (delete cube/light/camera), then create the object.
- Name the object descriptively from the prompt.
- Add a basic material with Principled BSDF.
- Set origin to geometry, apply transforms at the end where sensible.
- Wrap everything in a function `build()` and call it at the bottom.
- Include try/except and print clear status messages.
- Output ONLY the Python script, no markdown fences, no explanation.
";

}
