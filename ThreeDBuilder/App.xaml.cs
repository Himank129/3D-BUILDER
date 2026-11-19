using System.Windows;

namespace ThreeDBuilder;

/// <summary>
/// Application entry point. Interaction logic for App.xaml.
/// </summary>
public partial class App : Application
{
    public App()
    {
        // Ensure the external data directories exist on startup.
        Core.Paths.EnsureDefaultDirectories();
    }
}
