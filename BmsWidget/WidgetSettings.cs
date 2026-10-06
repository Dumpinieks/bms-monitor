using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmsWidget;

/// <summary>Stored in %LOCALAPPDATA%\BmsWidget\settings.json; edit the file and restart the widget to change.</summary>
sealed class WidgetSettings
{
    public static readonly string Directory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BmsWidget");
    static readonly string FilePath = Path.Combine(Directory, "settings.json");
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>BMS Bluetooth address (AA:BB:CC:DD:EE:FF); null searches automatically.</summary>
    public string? Address { get; set; }
    /// <summary>Search for a device whose name contains this text instead of the built-in heuristics.</summary>
    public string? Name { get; set; }
    /// <summary>The BMS found last time (set automatically); tried first when searching.</summary>
    public string? LastAddress { get; set; }
    public int Threshold { get; set; } = 25;
    public int Hysteresis { get; set; } = 5;
    public int IntervalSeconds { get; set; } = 5;
    public int EstimateWindowMinutes { get; set; } = 5;
    public int OfflineAlertMinutes { get; set; }
    public bool ShowPanel { get; set; } = true;
    /// <summary>Distance in pixels between the taskbar's right edge and the panel; null places it next to the tray.</summary>
    public int? PanelOffsetFromRight { get; set; }
    /// <summary>Same for a vertical taskbar: distance from its bottom edge; null places it above the tray.</summary>
    public int? PanelOffsetFromBottom { get; set; }

    public static WidgetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write($"Could not read {FilePath}: {ex.Message}. Using defaults.");
            return new();
        }

        var settings = new WidgetSettings();
        settings.Save(); // write defaults so the file is there to edit
        return settings;
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (IOException ex)
        {
            Log.Write($"Could not save {FilePath}: {ex.Message}");
        }
    }
}

static class Log
{
    static readonly string FilePath = Path.Combine(WidgetSettings.Directory, "log.txt");
    static readonly Lock Gate = new();
    const long MaxBytes = 1_000_000;

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                System.IO.Directory.CreateDirectory(WidgetSettings.Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
