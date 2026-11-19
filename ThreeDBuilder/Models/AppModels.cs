namespace ThreeDBuilder.Models;

/// <summary>
/// Top-level user settings stored encrypted in config.enc.
/// </summary>
public class AppSettings
{
    public int VaultVersion { get; set; } = 1;
    public Dictionary<string, string> ApiKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string BlenderPath { get; set; } = string.Empty;
    public string ModelsDir { get; set; } = string.Empty;
    public GenerationDefaults Defaults { get; set; } = new();
}

/// <summary>
/// Default generation preferences used across providers.
/// </summary>
public class GenerationDefaults
{
    public string Topology { get; set; } = "quad";   // quad | triangle
    public string Polycount { get; set; } = "medium"; // low | medium | high
    public string Format { get; set; } = "glb";       // glb | fbx | py
}

/// <summary>
/// One row in library.json — a generated model's metadata.
/// </summary>
public class LibraryEntry
{
    public string? Id { get; set; }
    public string Provider { get; set; } = string.Empty;   // tripo | huggingface | gemini | meshy | rodin
    public string? Prompt { get; set; }
    public string? ImagePath { get; set; }
    public string Topology { get; set; } = "quad";
    public string Polycount { get; set; } = "medium";
    public string Format { get; set; } = "glb";            // glb | fbx | py
    public string? FilePath { get; set; }                  // path to .glb / .fbx / .py
    public string? FileName { get; set; }
    public long FileSize { get; set; }
    public string? PythonScriptPath { get; set; }          // when Format == py
    public string? ThumbnailPath { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool BlenderOpened { get; set; }
    public Dictionary<string, string> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
