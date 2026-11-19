using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ThreeDBuilder.Core.Services;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Core.Storage;

/// <summary>
/// Manages the encrypted external config file (%APPDATA%/3D Builder/config.enc).
///
/// The .exe never stores API keys or settings internally. Everything is in config.enc,
/// encrypted with AES-256-GCM under a user vault password.
/// </summary>
public class ConfigManager : IDisposable
{
    private const int ConfigVersion = 1;

    private string? _vaultPassword;
    private AppSettings? _cached;
    private bool _disposed;

    public bool IsVaultUnlocked => _vaultPassword is not null;

    /// <summary>Unlock the vault. On first run (no config.enc) this creates the vault.</summary>
    public UnlockResult Unlock(string password)
    {
        if (!File.Exists(Paths.ConfigPath))
        {
            _vaultPassword = password;
            _cached = CreateDefault(password);
            Save();
            return new UnlockResult(ok: true, firstRun: true, error: null);
        }

        string ciphertext = File.ReadAllText(Paths.ConfigPath);
        try
        {
            string json = EncryptionService.Decrypt(ciphertext, password);
            var settings = JsonSerializer.Deserialize<AppSettings>(json)
                ?? throw new InvalidOperationException("Empty config.");
            _vaultPassword = password;
            _cached = settings;
            return new UnlockResult(ok: true, firstRun: false, error: null);
        }
        catch
        {
            return new UnlockResult(ok: false, firstRun: false, error: "Invalid vault password.");
        }
    }

    /// <summary>Read the full settings (requires unlocked vault).</summary>
    public AppSettings GetSettings()
    {
        RequireUnlocked();
        if (_cached is not null) return _cached;
        string ciphertext = File.ReadAllText(Paths.ConfigPath);
        string json = EncryptionService.Decrypt(ciphertext, _vaultPassword!);
        _cached = JsonSerializer.Deserialize<AppSettings>(json)
            ?? CreateDefault(_vaultPassword);
        return _cached;
    }

    /// <summary>Save settings (requires unlocked vault).</summary>
    public void SaveSettings(AppSettings settings)
    {
        RequireUnlocked();
        settings.VaultVersion = ConfigVersion;
        _cached = settings;
        Save();
    }

    /// <summary>Get a single API key (returns null if not set).</summary>
    public string? GetApiKey(string providerId)
    {
        RequireUnlocked();
        var s = GetSettings();
        return s.ApiKeys.TryGetValue(providerId, out var k) && !string.IsNullOrEmpty(k) ? k : null;
    }

    /// <summary>Set a single API key.</summary>
    public void SetApiKey(string providerId, string apiKey)
    {
        RequireUnlocked();
        var s = GetSettings();
        if (s.ApiKeys is null) s.ApiKeys = new Dictionary<string, string>();
        s.ApiKeys[providerId] = apiKey;
        SaveSettings(s);
    }

    /// <summary>Clear a single API key.</summary>
    public void ClearApiKey(string providerId)
    {
        RequireUnlocked();
        var s = GetSettings();
        if (s.ApiKeys is not null) s.ApiKeys.Remove(providerId);
        SaveSettings(s);
    }

    /// <summary>Get masked API keys for UI display (•••••••• or empty).</summary>
    public Dictionary<string, string> GetMaskedApiKeys()
    {
        RequireUnlocked();
        var s = GetSettings();
        var masked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (s.ApiKeys is not null)
        {
            foreach (var (provider, key) in s.ApiKeys)
                masked[provider] = string.IsNullOrEmpty(key) ? string.Empty : "••••••••";
        }
        return masked;
    }

    /// <summary>Get the user's models directory.</summary>
    public string GetModelsDir()
    {
        if (_vaultPassword is null) return Paths.DefaultModelsDir;
        var s = GetSettings();
        return string.IsNullOrEmpty(s.ModelsDir) ? Paths.DefaultModelsDir : s.ModelsDir;
    }

    /// <summary>Set the user's models directory.</summary>
    public void SetModelsDir(string dirPath)
    {
        RequireUnlocked();
        var s = GetSettings();
        s.ModelsDir = dirPath;
        SaveSettings(s);
        Directory.CreateDirectory(dirPath);
        Directory.CreateDirectory(Path.Combine(dirPath, "textures"));
    }

    /// <summary>Get the user's blender.exe path.</summary>
    public string GetBlenderPath()
    {
        RequireUnlocked();
        return GetSettings().BlenderPath ?? string.Empty;
    }

    /// <summary>Set the user's blender.exe path.</summary>
    public void SetBlenderPath(string blenderPath)
    {
        RequireUnlocked();
        var s = GetSettings();
        s.BlenderPath = blenderPath;
        SaveSettings(s);
    }

    /// <summary>Get default generation preferences.</summary>
    public GenerationDefaults GetDefaults()
    {
        RequireUnlocked();
        return GetSettings().Defaults;
    }

    /// <summary>Save default generation preferences.</summary>
    public void SetDefaults(GenerationDefaults defaults)
    {
        RequireUnlocked();
        var s = GetSettings();
        s.Defaults = defaults;
        SaveSettings(s);
    }

    private void Save()
    {
        if (_cached is null || _vaultPassword is null) return;
        Directory.CreateDirectory(Paths.AppDataDir);
        string json = JsonSerializer.Serialize(_cached, new JsonSerializerOptions { WriteIndented = true });
        string ciphertext = EncryptionService.Encrypt(json, _vaultPassword);
        File.WriteAllText(Paths.ConfigPath, ciphertext);
    }

    private AppSettings CreateDefault(string password)
    {
        return new AppSettings
        {
            VaultVersion = ConfigVersion,
            ApiKeys = new Dictionary<string, string>(),
            BlenderPath = string.Empty,
            ModelsDir = Paths.DefaultModelsDir,
            Defaults = new GenerationDefaults
            {
                Topology = "quad",
                Polycount = "medium",
                Format = "glb",
            },
        };
    }

    private void RequireUnlocked()
    {
        if (_vaultPassword is null)
            throw new InvalidOperationException("Vault not unlocked. Provide a vault password.");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _vaultPassword = null;
            _cached = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

public record UnlockResult(bool Ok, bool FirstRun, string? Error);
