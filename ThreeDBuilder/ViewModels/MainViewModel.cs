using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ThreeDBuilder.Core.Storage;
using ThreeDBuilder.Models;
using ThreeDBuilder.Providers;
using ThreeDBuilder.Services;

namespace ThreeDBuilder.ViewModels;

/// <summary>
/// Shell view model for the 3D Builder main window.
/// Wires together ConfigManager (encrypted external config), ProviderRegistry (BYOK adapters),
/// LibraryIndex (lightweight local index), and BlenderService (Open in Blender).
/// </summary>
public class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ---- Services ----
    public ConfigManager Config { get; }
    public LibraryIndex Library { get; }
    public BlenderService Blender { get; }

    // ---- UI state ----
    private string _view = "generate";
    public string View
    {
        get => _view;
        set { _view = value; OnPropertyChanged(); }
    }

    // Vault / login state
    private bool _vaultUnlocked;
    public bool VaultUnlocked
    {
        get => _vaultUnlocked;
        set { _vaultUnlocked = value; OnPropertyChanged(); }
    }

    // ---- Generation state ----
    public ObservableCollection<ProviderInfoItem> Providers { get; } = new();
    private ProviderInfoItem? _selectedProvider;
    public ProviderInfoItem? SelectedProvider
    {
        get => _selectedProvider;
        set { _selectedProvider = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    private string _inputMode = "text"; // text | image
    public string InputMode
    {
        get => _inputMode;
        set { _inputMode = value; OnPropertyChanged(); }
    }

    private string _prompt = string.Empty;
    public string Prompt
    {
        get => _prompt;
        set { _prompt = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    private string? _imagePath;
    public string? ImagePath
    {
        get => _imagePath;
        set { _imagePath = value; OnPropertyChanged(); }
    }

    private string _topology = "quad";
    public string Topology
    {
        get => _topology;
        set { _topology = value; OnPropertyChanged(); }
    }

    private string _polycount = "medium";
    public string Polycount
    {
        get => _polycount;
        set { _polycount = value; OnPropertyChanged(); }
    }

    private string _format = "glb";
    public string Format
    {
        get => _format;
        set { _format = value; OnPropertyChanged(); }
    }

    public bool CanGenerate => VaultUnlocked && SelectedProvider is not null && !string.IsNullOrWhiteSpace(Prompt);

    private bool _isGenerating;
    public bool IsGenerating
    {
        get => _isGenerating;
        set { _isGenerating = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanGenerate)); }
    }

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private int _progress;
    public int Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); }
    }

    // Last downloaded model (for Open in Blender from the generate view)
    private LibraryEntry? _lastModel;
    public LibraryEntry? LastModel
    {
        get => _lastModel;
        set { _lastModel = value; OnPropertyChanged(); }
    }

    public MainViewModel(ConfigManager config, LibraryIndex library, BlenderService blender)
    {
        Config = config;
        Library = library;
        Blender = blender;

        foreach (var p in ProviderRegistry.ListProviders())
            Providers.Add(new ProviderInfoItem(p.Id, p.Name, p.FreeTier));

        // Restore last-selected provider / defaults from settings
        try
        {
            var defaults = Config.GetDefaults();
            Topology = defaults.Topology;
            Polycount = defaults.Polycount;
            Format = defaults.Format;
        }
        catch { /* vault not yet unlocked */ }
    }

    // ---- Commands (exposed as methods for code-behind binding) ----

    public async Task UnlockVaultAsync(string password)
    {
        var result = Config.Unlock(password);
        if (result.Ok)
        {
            VaultUnlocked = true;
            // Refresh defaults from settings
            var defaults = Config.GetDefaults();
            Topology = defaults.Topology;
            Polycount = defaults.Polycount;
            Format = defaults.Format;
        }
        else
        {
            throw new Exception(result.Error ?? "Unlock failed.");
        }
    }

    public async Task GenerateAsync()
    {
        if (SelectedProvider is null || !CanGenerate) return;

        IsGenerating = true;
        Status = "Submitting job...";
        Progress = 0;

        try
        {
            string? apiKey = Config.GetApiKey(SelectedProvider.Id);
            ProviderRegistry.SetApiKey(SelectedProvider.Id, apiKey ?? string.Empty);

            var options = new GenerationOptions
            {
                Topology = Topology,
                Polycount = ProviderRegistry.ListProviders().First(p => p.Id == SelectedProvider.Id).Id == "gemini"
                    ? 30_000 : NormalizePolycountValue(),
                Format = Format,
            };

            var submit = await ProviderRegistry.SubmitGenerationJobAsync(
                SelectedProvider.Id, Prompt, ImagePath, options);

            Status = "Job submitted. Polling...";
            string jobId = submit.JobId;

            // Poll until completed/failed (with a cap)
            ProviderJobStatus status;
            int attempts = 0;
            while (attempts++ < 120)
            {
                await Task.Delay(2000);
                status = await ProviderRegistry.PollJobStatusAsync(SelectedProvider.Id, jobId);
                Progress = status.Progress;
                Status = $"{status.Status} ({status.Progress}%)";
                if (status.Status == "completed" || status.Status == "failed") break;
            }

            if (status.Status != "completed")
            {
                Status = $"Generation did not complete: {status.Status}. {status.Error ?? string.Empty}";
                return;
            }

            // Download to user's Models folder
            string modelsDir = Config.GetModelsDir();
            var download = await ProviderRegistry.DownloadToLocalFolderAsync(SelectedProvider.Id, jobId, modelsDir);

            // Add to library index
            var entry = Library.Add(new LibraryEntry
            {
                Provider = SelectedProvider.Id,
                Prompt = Prompt,
                ImagePath = ImagePath,
                Topology = Topology,
                Polycount = Polycount,
                Format = download.FilePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase) ? "py" : Format,
                FilePath = download.FilePath,
                FileName = download.FileName,
                FileSize = download.FileSize,
                PythonScriptPath = download.PythonScriptPath,
                Extra = new Dictionary<string, string>
                {
                    ["mime"] = download.Mime,
                    ["provider"] = download.Provider,
                },
            });

            LastModel = entry;
            Status = $"Downloaded: {download.FileName}";
            Progress = 100;

            // Persist defaults
            Config.SetDefaults(new GenerationDefaults
            {
                Topology = Topology,
                Polycount = Polycount,
                Format = Format,
            });
        }
        catch (Exception ex)
        {
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsGenerating = false;
        }
    }

    private int NormalizePolycountValue() => Polycount switch
    {
        "low" => 10_000,
        "high" => 100_000,
        _ => 30_000
    };

    public async Task OpenInBlenderAsync(LibraryEntry entry)
    {
        var result = await Blender.OpenAsync(entry.FilePath ?? string.Empty);
        if (result.Ok)
        {
            Library.MarkBlenderOpened(entry.Id ?? string.Empty);
            Status = $"Opened in Blender: {entry.FileName}";
        }
        else
        {
            Status = "Blender error: " + result.Error;
            throw new Exception(result.Error);
        }
    }

    public async Task SetApiKeyAsync(string providerId, string apiKey)
    {
        Config.SetApiKey(providerId, apiKey);
        ProviderRegistry.SetApiKey(providerId, apiKey);
    }

    public async Task ClearApiKeyAsync(string providerId)
    {
        Config.ClearApiKey(providerId);
    }

    public async Task SetBlenderPathAsync(string path) => Config.SetBlenderPath(path);

    public async Task SetModelsDirAsync(string path) => Config.SetModelsDir(path);
}

public class ProviderInfoItem
{
    public string Id { get; }
    public string Name { get; }
    public bool FreeTier { get; }
    public string DisplayName => $"{Name}" + (FreeTier ? " (Free)" : " (Paid)");
    public ProviderInfoItem(string id, string name, bool freeTier)
    {
        Id = id; Name = name; FreeTier = freeTier;
    }
}
