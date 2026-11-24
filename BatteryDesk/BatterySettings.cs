using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatteryDesk.Core;

namespace BatteryDesk;

internal enum TaskbarStyle { Percentage, Meter, Hub }

internal static class BatterySettings
{
    internal static readonly string DataDirectory = Environment.GetEnvironmentVariable("BATTERYDESK_DATA_DIR") is { Length: > 0 } testDirectory
        ? Path.GetFullPath(testDirectory)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryDesk");
    private static readonly string PathToSettings = Path.Combine(DataDirectory, "settings.json");

    public static int ReadLowThreshold()
    {
        try
        {
            if (File.Exists(PathToSettings))
            {
                using var stream = File.OpenRead(PathToSettings);
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("lowBatteryPercent", out var property)
                    && property.ValueKind == JsonValueKind.Number
                    && property.TryGetInt32(out var threshold) && threshold is >= 1 and <= 99)
                    return threshold;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not load BatteryDesk settings: {0}", error.Message);
        }
        return BatteryState.DefaultLowThreshold;
    }

    public static TaskbarStyle ReadTaskbarStyle()
    {
        try
        {
            if (ReadObject()["taskbarStyle"] is JsonValue value && value.TryGetValue<string>(out var name))
                return name switch { "Meter" => TaskbarStyle.Meter, "Hub" => TaskbarStyle.Hub, _ => TaskbarStyle.Percentage };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not load BatteryDesk display style: {0}", error.Message);
        }
        return TaskbarStyle.Percentage;
    }

    public static bool SaveTaskbarStyle(TaskbarStyle style)
    {
        var temporary = PathToSettings + ".tmp";
        try
        {
            var settings = ReadObject();
            settings["taskbarStyle"] = style.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(PathToSettings)!);
            File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, PathToSettings, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Could not save BatteryDesk display style: {0}", error.Message);
            return false;
        }
    }

    private static JsonObject ReadObject() => File.Exists(PathToSettings)
        ? JsonNode.Parse(File.ReadAllText(PathToSettings)) as JsonObject ?? throw new JsonException("Expected a settings object.")
        : new JsonObject();
}
