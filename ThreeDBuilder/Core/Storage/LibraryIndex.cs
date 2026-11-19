using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ThreeDBuilder.Models;

namespace ThreeDBuilder.Core.Storage;

/// <summary>
/// Lightweight local index of generated models (Documents/3D Builder/library.json).
/// Keeps the .exe launch fast and RAM usage low — models themselves are on disk,
/// not bundled or kept in memory.
/// </summary>
public class LibraryIndex : IDisposable
{
    private readonly string _libraryPath;
    private Library _cache;
    private bool _disposed;

    public LibraryIndex(string modelsDir)
    {
        Directory.CreateDirectory(modelsDir);
        string appDir = Path.GetDirectoryName(modelsDir) ?? Paths.DocumentsAppDir;
        _libraryPath = Path.Combine(appDir, "library.json");
        _cache = Load();
    }

    public List<LibraryEntry> Models => _cache.Models;

    public LibraryEntry? Get(string id) => _cache.Models.Find(m => m.Id == id);

    public LibraryEntry Add(LibraryEntry entry)
    {
        entry.Id ??= Guid.NewGuid().ToString();
        entry.CreatedAt ??= DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;
        entry.BlenderOpened = false;

        // Remove any existing entry with same id, then insert at top.
        _cache.Models.RemoveAll(m => m.Id == entry.Id);
        _cache.Models.Insert(0, entry);
        Save();
        return entry;
    }

    public LibraryEntry? Update(string id, Action<LibraryEntry> mutate)
    {
        var entry = _cache.Models.Find(m => m.Id == id);
        if (entry is null) return null;
        mutate(entry);
        entry.UpdatedAt = DateTime.UtcNow;
        Save();
        return entry;
    }

    public bool Remove(string id)
    {
        int before = _cache.Models.Count;
        _cache.Models.RemoveAll(m => m.Id == id);
        if (_cache.Models.Count < before)
        {
            Save();
            return true;
        }
        return false;
    }

    public void MarkBlenderOpened(string id)
    {
        var entry = _cache.Models.Find(m => m.Id == id);
        if (entry is not null)
        {
            entry.BlenderOpened = true;
            entry.UpdatedAt = DateTime.UtcNow;
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_libraryPath)!);
        string json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_libraryPath, json);
    }

    private Library Load()
    {
        if (!File.Exists(_libraryPath))
            return new Library { Version = 1, Models = new List<LibraryEntry>() };
        try
        {
            string json = File.ReadAllText(_libraryPath);
            var lib = JsonSerializer.Deserialize<Library>(json)
                ?? new Library { Version = 1, Models = new List<LibraryEntry>() };
            lib.Models ??= new List<LibraryEntry>();
            return lib;
        }
        catch
        {
            return new Library { Version = 1, Models = new List<LibraryEntry>() };
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

public record Library(int Version, List<LibraryEntry> Models);
