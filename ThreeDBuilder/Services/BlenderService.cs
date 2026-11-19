using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ThreeDBuilder.Core.Storage;

namespace ThreeDBuilder.Services;

/// <summary>
/// Direct "Open in Blender" integration.
/// Launches the user's local blender.exe via CLI and:
///   • imports a .glb/.fbx file, or
///   • runs a generated .py (bpy) script that builds modifier-based geometry.
/// The blender.exe path is stored encrypted in config.enc and user-selectable in Settings.
/// </summary>
public class BlenderService
{
    private readonly ConfigManager _config;

    public BlenderService(ConfigManager config) => _config = config;

    /// <summary>Resolve blender.exe from config, falling back to common Windows install locations.</summary>
    public string? ResolveBlenderPath()
    {
        var configured = _config.GetBlenderPath();
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return configured;

        var common = new[]
        {
            @"C:\Program Files\Blender Foundation\Blender 4.4\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.3\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.1\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender 4.0\blender.exe",
            @"C:\Program Files\Blender Foundation\Blender\blender.exe",
        };
        foreach (var p in common) if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Open a mesh (.glb/.fbx) or run a .py script in Blender.</summary>
    public async Task<(bool Ok, string? Error, int? Pid)> OpenAsync(string filePath, string? blenderPathOverride = null)
    {
        return await Task.Run(() =>
        {
            var blender = blenderPathOverride ?? ResolveBlenderPath();
            if (blender is null)
                return (false, "Blender not found. Set your blender.exe path in Settings (e.g. C:\\Program Files\\Blender Foundation\\Blender 4.x\\blender.exe).", (int?)null);
            if (!File.Exists(filePath))
                return (false, "File not found: " + filePath, (int?)null);

            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isScript = ext == ".py";
            string script = isScript ? BuildRunScript(filePath) : BuildImportScript(filePath);

            // Write a temp runner next to the user's models folder (never inside the .exe).
            var modelsDir = _config.GetModelsDir();
            var runnerDir = Path.GetDirectoryName(modelsDir)!;
            var runnerPath = Path.Combine(runnerDir, "_3d_builder_blender_runner.py");
            File.WriteAllText(runnerPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = blender,
                Arguments = $"--background --python \"{runnerPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
                return (false, "Failed to launch Blender.", (int?)null);

            var pid = proc.Id;
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            if (proc.ExitCode == 0 && (stdout.Contains("OK:") || stdout.Contains("IMPORT_OK") || stdout.Contains("SCRIPT_OK")))
                return (true, null, pid);

            return (false, stderr.Length > 0 ? stderr : stdout.Length > 0 ? stdout : $"Blender exited {proc.ExitCode}", pid);
        });
    }

    /// <summary>Launch Blender empty (no import).</summary>
    public async Task<(bool Ok, string? Error, int? Pid)> LaunchAsync(string? blenderPathOverride = null)
    {
        return await Task.Run(() =>
        {
            var blender = blenderPathOverride ?? ResolveBlenderPath();
            if (blender is null)
                return (false, "Blender not found. Set your blender.exe path in Settings.", (int?)null);
            var psi = new ProcessStartInfo
            {
                FileName = blender,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Failed to launch Blender.", (int?)null);
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            return (true, null, proc.Id);
        });
    }

    private static string BuildImportScript(string filePath)
    {
        var abs = Path.GetFullPath(filePath);
        // C# verbatim: to emit a " character we write "".
        return "import bpy, os, sys\n" +
               "filepath = r\"" + abs + "\"\n" +
               "if not os.path.exists(filepath):\n" +
               "    print(\"ERROR: file not found: \" + filepath)\n" +
               "    sys.exit(1)\n" +
               "ext = os.path.splitext(filepath)[1].lower()\n" +
               "bpy.ops.object.select_all(action='SELECT')\n" +
               "bpy.ops.object.delete(use_global=False)\n" +
               "if ext in ('.glb', '.gltf'):\n" +
               "    bpy.ops.import_scene.gltf(filepath=filepath)\n" +
               "elif ext == '.fbx':\n" +
               "    bpy.ops.import_scene.fbx(filepath=filepath)\n" +
               "else:\n" +
               "    print(\"ERROR: unsupported format \" + ext)\n" +
               "    sys.exit(1)\n" +
               "print(\"IMPORT_OK:\" + filepath)\n";
    }

    private static string BuildRunScript(string pyPath)
    {
        var abs = Path.GetFullPath(pyPath);
        return "import bpy, os, sys\n" +
               "filepath = r\"" + abs + "\"\n" +
               "if not os.path.exists(filepath):\n" +
               "    print(\"ERROR: script not found: \" + filepath)\n" +
               "    sys.exit(1)\n" +
               "with open(filepath, 'r', encoding='utf-8') as f:\n" +
               "    code = f.read()\n" +
               "ns = {'bpy': bpy, 'os': os}\n" +
               "exec(compile(code, filepath, 'exec'), ns)\n" +
               "print(\"SCRIPT_OK:\" + filepath)\n";
    }
}
