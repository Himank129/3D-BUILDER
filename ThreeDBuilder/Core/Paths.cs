using System;
using System.IO;

namespace ThreeDBuilder.Core;

/// <summary>
/// All user data lives OUTSIDE the 3D Builder.exe binary.
///
///   %APPDATA%/3D Builder/config.enc        -> encrypted settings + BYOK API keys
///   %USERPROFILE%/Documents/3D Builder/    -> library.json + Models/
///   .../Models/*.glb, .fbx, .py, textures/ -> generated assets
///
/// Nothing is bundled inside the .exe.
/// </summary>
public static class Paths
{
    public const string AppName = "3D Builder";

    /// <summary>%APPDATA%/3D Builder</summary>
    public static string AppDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    /// <summary>%APPDATA%/3D Builder/config.enc</summary>
    public static string ConfigPath => Path.Combine(AppDataDir, "config.enc");

    /// <summary>%USERPROFILE%/Documents/3D Builder</summary>
    public static string DocumentsAppDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), AppName);

    /// <summary>Default: %USERPROFILE%/Documents/3D Builder/Models</summary>
    public static string DefaultModelsDir =>
        Path.Combine(DocumentsAppDir, "Models");

    /// <summary>Default: %USERPROFILE%/Documents/3D Builder/Models/textures</summary>
    public static string DefaultTexturesDir =>
        Path.Combine(DefaultModelsDir, "textures");

    /// <summary>Default: %USERPROFILE%/Documents/3D Builder/library.json</summary>
    public static string DefaultLibraryPath =>
        Path.Combine(DocumentsAppDir, "library.json");

    public static void EnsureDefaultDirectories()
    {
        Directory.CreateDirectory(AppDataDir);
        Directory.CreateDirectory(DefaultModelsDir);
        Directory.CreateDirectory(DefaultTexturesDir);
        Directory.CreateDirectory(DocumentsAppDir);
    }
}
