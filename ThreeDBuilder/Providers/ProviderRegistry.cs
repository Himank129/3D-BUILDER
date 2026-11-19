using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Providers;

/// <summary>
/// Provider registry & factory. Exposes the unified interface:
///   ValidateKey / SubmitGenerationJob / PollJobStatus / DownloadToLocalFolder
///
/// Adapters are created lazily with the user's BYOK API key from encrypted config.enc.
/// </summary>
public static class ProviderRegistry
{
    private static readonly Dictionary<string, Type> _providerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tripo"] = typeof(Providers.Tripo.TripoProvider),
        ["huggingface"] = typeof(Providers.HuggingFace.HuggingFaceProvider),
        ["gemini"] = typeof(Providers.Gemini.GeminiProvider),
        ["meshy"] = typeof(Providers.Meshy.MeshyProvider),
        ["rodin"] = typeof(Providers.Rodin.RodinProvider),
    };

    private static readonly Dictionary<string, BaseProvider> _instances = new(StringComparer.OrdinalIgnoreCase);

    public record ProviderInfo(string Id, string Name, bool FreeTier);

    public static IReadOnlyList<ProviderInfo> ListProviders() => new[]
    {
        new ProviderInfo("tripo", "Tripo AI", true),
        new ProviderInfo("huggingface", "Hugging Face (ZeroGPU)", true),
        new ProviderInfo("gemini", "Google Gemini", false),
        new ProviderInfo("meshy", "Meshy AI", false),
        new ProviderInfo("rodin", "Rodin / Hyper3D", false),
    };

    public static BaseProvider GetOrCreate(string providerId, string apiKey)
    {
        if (!_providerTypes.TryGetValue(providerId, out var type))
            throw new ArgumentException($"Unknown provider: {providerId}");

        if (!_instances.TryGetValue(providerId, out var inst) || inst == null)
        {
            inst = (BaseProvider)Activator.CreateInstance(type, providerId, name: type.Name, apiKey: apiKey)!;
            _instances[providerId] = inst;
        }
        else
        {
            inst.ApiKey = apiKey;
        }
        return inst;
    }

    public static async Task<ProviderKeyValidationResult> ValidateKeyAsync(string providerId, string apiKey)
    {
        var inst = GetOrCreate(providerId, apiKey);
        return await inst.ValidateKeyAsync(apiKey);
    }

    public static async Task<ProviderSubmitResult> SubmitGenerationJobAsync(
        string providerId, string prompt, string? imagePath, GenerationOptions options)
    {
        var inst = GetOrCreate(providerId, string.Empty); // key must be set via SetApiKey before submit
        inst.TrackJob(options.JobId, new ProviderJobStatus
        {
            JobId = options.JobId,
            Status = "queued",
            Progress = 0,
            Provider = providerId,
        });
        var result = await inst.SubmitGenerationJobAsync(prompt, imagePath, options);
        result.JobId = options.JobId;
        return result;
    }

    public static async Task<ProviderJobStatus> PollJobStatusAsync(string providerId, string jobId)
    {
        var inst = GetOrCreate(providerId, string.Empty);
        var status = await inst.PollJobStatusAsync(jobId);
        status.Provider = providerId;
        status.JobId = jobId;
        var tracked = inst.GetJob(jobId);
        if (tracked != null)
        {
            tracked.Status = status.Status;
            tracked.Progress = status.Progress;
            tracked.Result = status.Result;
            tracked.Error = status.Error;
        }
        return status;
    }

    public static async Task<ProviderDownloadResult> DownloadToLocalFolderAsync(
        string providerId, string jobId, string outputDir)
    {
        var inst = GetOrCreate(providerId, inst.ApiKey);
        return await inst.DownloadToLocalFolderAsync(jobId, outputDir);
    }

    public static void SetApiKey(string providerId, string apiKey) => GetOrCreate(providerId, apiKey);
}
