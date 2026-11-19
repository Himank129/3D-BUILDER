using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using ThreeDBuilder.Core.Storage;
using ThreeDBuilder.Models;
using ThreeDBuilder.Services;
using ThreeDBuilder.ViewModels;

namespace ThreeDBuilder.Views;

/// <summary>
/// Main shell window for 3D Builder.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();

        // External storage directories are ensured by App startup.
        var config = new ConfigManager();
        var library = new LibraryIndex(config.GetModelsDir());
        var blender = new BlenderService(config);
        _vm = new MainViewModel(config, library, blender);

        DataContext = _vm;

        // Load persisted defaults into the UI (read-only until vault unlocked).
        try
        {
            var defaults = config.GetDefaults();
            CmbTopology.SelectedValue = defaults.Topology;
            CmbPolycount.SelectedValue = defaults.Polycount;
            CmbFormat.SelectedValue = defaults.Format;
        }
        catch { }

        try { TxtBlenderPath.Text = config.GetBlenderPath(); } catch { }
        try { TxtModelsDir.Text = config.GetModelsDir(); } catch { }

        UpdateVaultStatus(false);
        SelectTab("generate");
    }

    // ---------- Navigation ----------

    private void SelectTab(string view)
    {
        GeneratePanel.Visibility = view == "generate" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = view == "library" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = view == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (view == "library") RefreshLibrary();
    }

    private void BtnGenerate_Click(object sender, RoutedEventArgs e) => SelectTab("generate");
    private void BtnLibrary_Click(object sender, RoutedEventArgs e) => SelectTab("library");
    private void BtnSettings_Click(object sender, RoutedEventArgs e) => SelectTab("settings");

    // ---------- Vault ----------

    private async void BtnUnlock_Click(object sender, RoutedEventArgs e)
    {
        var password = PromptForVaultPassword();
        if (password is null) return;
        try
        {
            await _vm.UnlockVaultAsync(password);
            UpdateVaultStatus(true);
            RefreshApiKeys();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Unlock failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private string? PromptForVaultPassword()
    {
        var dialog = new Window
        {
            Title = "Unlock Vault",
            Width = 420, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "Enter your vault password (created on first run):", Margin = new Thickness(0, 0, 0, 10) });
        var pwd = new PasswordBox { Margin = new Thickness(0, 0, 0, 14), Height = 30 };
        panel.Children.Add(pwd);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "Unlock", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);
        dialog.Content = panel;

        string? result = null;
        ok.Click += (_, _) => { result = pwd.Password; dialog.DialogResult = true; };
        cancel.Click += (_, _) => { dialog.DialogResult = false; };
        dialog.ShowDialog();
        return result;
    }

    private void UpdateVaultStatus(bool unlocked)
    {
        VaultStatusText.Text = unlocked ? "Vault: unlocked" : "Vault: locked";
        VaultStatusText.Foreground = unlocked ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Orange;
        BtnUnlock.Content = unlocked ? "Vault Unlocked" : "Unlock Vault";
        BtnUnlock.IsEnabled = !unlocked;
    }

    private void RefreshApiKeys()
    {
        try
        {
            var masked = _vm.Config.GetMaskedApiKeys();
            TxtApiStatus.Text = string.Join("  |  ", masked.Select(kv => $"{kv.Key}: {(string.IsNullOrEmpty(kv.Value) ? "not set" : "••••••••")}"));
        }
        catch
        {
            TxtApiStatus.Text = "Unlock vault to manage API keys.";
        }
    }

    // ---------- Generation ----------

    private void CmbProvider_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbProvider.SelectedValue is string id)
        {
            _vm.SelectedProvider = _vm.Providers.FirstOrDefault(p => p.Id == id);
        }
    }

    private async void BtnGenerateModel_Click(object sender, RoutedEventArgs e)
    {
        _vm.Prompt = TxtPrompt.Text;
        _vm.Topology = CmbTopology.SelectedValue?.ToString() ?? "quad";
        _vm.Polycount = CmbPolycount.SelectedValue?.ToString() ?? "medium";
        _vm.Format = CmbFormat.SelectedValue?.ToString() ?? "glb";
        _vm.ImagePath = TxtImagePath.Text;

        await _vm.GenerateAsync();

        TxtStatus.Text = _vm.Status;
        PrgProgress.Value = _vm.Progress;

        if (_vm.LastModel is { } last)
        {
            TxtLastModel.Text = $"{last.FileName}  ({last.Provider}, {last.Format})\n{last.FilePath}";
            BtnOpenLastInBlender.IsEnabled = true;
        }
    }

    private void BtnBrowseImage_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog
        {
            Title = "Select reference image",
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp|All files (*.*)|*.*"
        };
        if (d.ShowDialog(this) == true)
        {
            TxtImagePath.Text = d.FileName;
            _vm.ImagePath = d.FileName;
        }
    }

    private async void BtnOpenLastInBlender_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.LastModel is null) return;
        try
        {
            await _vm.OpenInBlenderAsync(_vm.LastModel);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open in Blender", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Library ----------

    private void RefreshLibrary()
    {
        LibraryList.ItemsSource = null;
        LibraryList.ItemsSource = _vm.Library.Models;
    }

    private void BtnRefreshLibrary_Click(object sender, RoutedEventArgs e) => RefreshLibrary();

    private async void BtnOpenLibraryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is LibraryEntry entry)
        {
            try { await _vm.OpenInBlenderAsync(entry); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Open in Blender", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    private async void BtnRemoveLibraryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is LibraryEntry entry)
        {
            if (MessageBox.Show($"Remove \"{entry.FileName}\" from library?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _vm.Library.Remove(entry.Id ?? string.Empty);
                try
                {
                    if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath))
                        File.Delete(entry.FilePath);
                }
                catch { }
                RefreshLibrary();
            }
        }
    }

    // ---------- Settings ----------

    private void ApiKeyPassword_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox pwd && pwd.Tag is string providerId)
        {
            try { _vm.SetApiKeyAsync(providerId, pwd.Password).Wait(); } catch { }
        }
    }

    private async void BtnBrowseBlender_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFileDialog { Title = "Select blender.exe", Filter = "blender.exe|blender.exe|All files (*.*)|*.*" };
        if (d.ShowDialog(this) == true)
        {
            TxtBlenderPath.Text = d.FileName;
            await _vm.SetBlenderPathAsync(d.FileName);
        }
    }

    private async void BtnTestBlender_Click(object sender, RoutedEventArgs e)
    {
        var path = TxtBlenderPath.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            MessageBox.Show("Set a blender.exe path first.", "Blender", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await _vm.SetBlenderPathAsync(path);
        var blender = (BlenderService)_vm.GetType().GetProperty("Blender")!.GetValue(_vm)!;
        var result = await blender.LaunchAsync(path);
        TxtBlenderStatus.Text = result.Ok ? "Blender launched successfully." : "Failed: " + result.Error;
    }

    private async void BtnBrowseModelsDir_Click(object sender, RoutedEventArgs e)
    {
        var fbd = new FolderBrowserDialog { Description = "Select the folder where 3D models are saved." };
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtModelsDir.Text = fbd.SelectedPath;
            await _vm.SetModelsDirAsync(fbd.SelectedPath);
        }
    }

    private async void BtnSaveDefaults_Click(object sender, RoutedEventArgs e)
    {
        _vm.Topology = CmbTopology.SelectedValue?.ToString() ?? "quad";
        _vm.Polycount = CmbPolycount.SelectedValue?.ToString() ?? "medium";
        _vm.Format = CmbFormat.SelectedValue?.ToString() ?? "glb";
        try
        {
            _vm.Config.SetDefaults(new GenerationDefaults
            {
                Topology = _vm.Topology,
                Polycount = _vm.Polycount,
                Format = _vm.Format,
            });
            TxtDefaultsStatus.Text = "Defaults saved.";
        }
        catch (Exception ex) { TxtDefaultsStatus.Text = "Save failed: " + ex.Message; }
    }
}
