using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoEmpty.UI;

public sealed class UiSettings
{
    public List<string> RecentProjects { get; set; } = new();
    public bool AutoDeleteBackupsEnabled { get; set; }
    public int AutoDeleteBackupsDays { get; set; } = 90;
    public bool SnapToGridEnabled { get; set; }
    public int SnapGridDivisions { get; set; } = 10;
}

public static class UiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string AppDataDir
    {
        get
        {
            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(baseDir, "VideoEmpty");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string SettingsPath => Path.Combine(AppDataDir, "ui-settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new UiSettings();
            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new UiSettings();
        }
        catch
        {
            return new UiSettings();
        }
    }

    public static void Save(UiSettings settings)
    {
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public static string EnsureProjectRoot()
    {
        var dir = Path.Combine(AppDataDir, "projects");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static string GetProjectPath(string projectName)
    {
        var safe = string.Join("_", projectName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safe)) safe = "project";
        return Path.Combine(EnsureProjectRoot(), $"{safe}.veproj");
    }

    public static string GetBackupDir(string projectPath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectPath))).Substring(0, 10);
        var safe = Path.GetFileNameWithoutExtension(projectPath);
        var dir = Path.Combine(AppDataDir, "backups", $"{safe}_{hash}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
