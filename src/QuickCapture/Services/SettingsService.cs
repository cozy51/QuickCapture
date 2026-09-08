using System;
using System.IO;
using System.Text.Json;
using QuickCapture.Models;

namespace QuickCapture.Services;

public sealed class SettingsService
{
    public string FilePath { get; }
    public string? LoadWarning { get; private set; }
    public SettingsService(string? filePath = null) => FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickCapture", "settings.json");
    public AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? throw new JsonException();
            settings.Validate(); return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            LoadWarning = "設定を読み込めなかったため初期値を使用しています。設定ファイルは保持しています。";
            return new();
        }
    }
    public void Save(AppSettings settings)
    {
        settings.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, FilePath, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
