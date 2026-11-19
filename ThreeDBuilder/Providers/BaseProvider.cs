using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Providers;

/// <summary>
/// Standardized result of validating a provider API key.
/// </summary>
public class ProviderKeyValidationResult
{
    public bool Valid { get; set; }
    public string? Error { get; set; }
    public object? Balance { get; set; }      // provider-specific balance/credits info
    public string? Message { get; set; }
}

/// <summary>
/// Standardized result of submitting a generation job.
/// </summary>
public class ProviderSubmitResult
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "queued"; // queued | running | completed | failed | downloaded
    public int Progress { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? ExternalJobId { get; set; }
    public object? Raw { get; set; }
}

/// <summary>
/// Standardized job status.
/// </summary>
public class ProviderJobStatus
{
    public string JobId { get; set; } = string.Empty;
    public string Status { get; set; } = "queued";
    public int Progress { get; set; }
    public object? Result { get; set; }
    public string? Error { get; set; }
    public string Provider { get; set; } = string.Empty;
}

/// <summary>
/// Standardized download result into the user's local Models folder.
/// </summary>
public class ProviderDownloadResult
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string Mime { get; set; } = "application/octet-stream";
    public string? PythonScriptPath { get; set; }
    public string Provider { get; set; } = string.Empty;
}

/// <summary>
/// Options shared across all providers for a generation job.
/// </summary>
public class GenerationOptions
{
    public string JobId { get; set; } = Guid.NewGuid().ToString();
    public string Topology { get; set; } = "quad";    // quad | triangle
    public int Polycount { get; set; } = 30_000;      // 10k | 30k | 100k
    public string Format { get; set; } = "glb";       // glb | fbx | py
    public string? ExternalJobId { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Base class every AI provider adapter implements. Providers are BYOK — the user
/// supplies API keys in Settings (stored encrypted in config.enc). Nothing is
/// bundled inside 3D Builder.exe.
/// </summary>
public abstract class BaseProvider
{
    protected BaseProvider(string providerId, string name, string apiKey = "")
    {
        ProviderId = providerId;
        Name = name;
        ApiKey = apiKey;
        ActiveJobs = new Dictionary<string, ProviderJobStatus>(StringComparer.OrdinalIgnoreCase);
    }

    public string ProviderId { get; }
    public string Name { get; }
    public string ApiKey { get; set; }

    protected Dictionary<string, ProviderJobStatus> ActiveJobs { get; }

    /// <summary>Validate the API key and optionally return balance/credits.</summary>
    public abstract Task<ProviderKeyValidationResult> ValidateKeyAsync(string apiKey);

    /// <summary>Submit a text-to-3D or image-to-3D job.</summary>
    public abstract Task<ProviderSubmitResult> SubmitGenerationJobAsync(string prompt, string? imagePath, GenerationOptions options);

    /// <summary>Poll the status of an in-flight job.</summary>
    public abstract Task<ProviderJobStatus> PollJobStatusAsync(string jobId);

    /// <summary>Download a finished result into outputDir (user's Models folder).</summary>
    public abstract Task<ProviderDownloadResult> DownloadToLocalFolderAsync(string jobId, string outputDir);

    /// <summary>Helper: normalize topology.</summary>
    protected string NormalizeTopology(string? topology) => string.Equals(topology, "triangle", StringComparison.OrdinalIgnoreCase) ? "triangle" : "quad";

    /// <summary>Helper: normalize polycount label -> target value.</summary>
    protected int NormalizePolycount(string? polycount) => polycount switch
    {
        "low" => 10_000,
        "high" => 100_000,
        _ => 30_000
    };

    /// <summary>Helper: normalize export format.</summary>
    protected string NormalizeFormat(string? format)
    {
        var f = (format ?? "glb").ToLowerInvariant();
        return f is "glb" or "fbx" or "py" ? f : "glb";
    }

    /// <summary>Track a job for polling.</summary>
    protected void TrackJob(string jobId, ProviderJobStatus status) => ActiveJobs[jobId] = status;

    /// <summary>Get a tracked job.</summary>
    protected ProviderJobStatus? GetJob(string jobId) => ActiveJobs.TryGetValue(jobId, out var j) ? j : null;

    /// <summary>Ensure output dir exists and build a safe output path.</summary>
    protected string MakeOutputPath(string outputDir, string fileName, string ext)
    {
        Directory.CreateDirectory(outputDir);
        var safe = string.Join("_", fileName.Split(Path.GetInvalidFileNameChars())).TrimEnd('.').Slice(0, 80);
        return Path.Combine(outputDir, $"{safe}.{ext}");
    }
}

public static class StringExtensions
{
    public static string Slice(this string s, int maxLength) => s.Length > maxLength ? s[..maxLength] : s;
}
