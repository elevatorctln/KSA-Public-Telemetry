using System.Text.Json;
using System.Text.Json.Serialization;
using KSA;
using KSATelemetryOverlay.Rendering;

namespace KSATelemetryOverlay.Config;

public static class ConfigStore
{
    private const string LogPrefix = "[KSATelemetryOverlay] ";
    private const string FolderName = "KSATelemetryOverlay";
    private const string FileName = "config.json";
    private const double SaveIntervalSeconds = 1.0;

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        IncludeFields = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    private static bool _dirty;
    private static double _sinceChange;
    private static OverlayConfig? _tracked;
    public static string DirectoryPath => Path.Combine(Constants.DocumentsFolderPath, FolderName);

    public static string FilePath => Path.Combine(DirectoryPath, FileName);

    private sealed class ConfigFile
    {
        [JsonPropertyName("settings")]
        public OverlayConfig? Settings { get; set; }

        [JsonPropertyName("colors")]
        public Dictionary<string, string>? Colors { get; set; }

        [JsonPropertyName("tuning")]
        public Dictionary<string, double>? Tuning { get; set; }
    }
    public static OverlayConfig Load()
    {
        OverlayConfig config = new();

        try
        {
            string path = FilePath;

            if (!File.Exists(path))
            {
                OverlayPalette.ResetAll();
                Rendering.Tuning.ResetAll();
                Track(config);
                Console.WriteLine(LogPrefix + $"no config at {path}; using defaults.");
                return config;
            }

            ConfigFile? file = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(path), _options);

            if (file?.Settings is { } loaded)
            {
                config = loaded;
                Sanitise(config);
            }

            OverlayPalette.ApplyOverrides(file?.Colors);
            Rendering.Tuning.ApplyOverrides(file?.Tuning);
            Console.WriteLine(LogPrefix + $"loaded config from {path}.");
        }
        catch (Exception ex)
        {
            config = new OverlayConfig();
            OverlayPalette.ResetAll();
            Rendering.Tuning.ResetAll();
            Console.WriteLine(LogPrefix + $"config load failed, using defaults: {ex.Message}");
        }

        Track(config);
        return config;
    }

    public static void Track(OverlayConfig config) => _tracked = config;

    public static void MarkDirty()
    {
        _dirty = true;
        _sinceChange = 0.0;
    }

    public static void Flush(double dt)
    {
        if (!_dirty)
        {
            return;
        }

        _sinceChange += dt;
        if (_sinceChange < SaveIntervalSeconds)
        {
            return;
        }

        SaveNow();
    }

    public static void SaveNow()
    {
        _dirty = false;
        _sinceChange = 0.0;

        if (_tracked is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DirectoryPath);

            ConfigFile file = new()
            {
                Settings = _tracked,
                Colors = OverlayPalette.CaptureOverrides(),
                Tuning = Rendering.Tuning.CaptureOverrides(),
            };

            string path = FilePath;
            string temp = path + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(file, _options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"config save failed: {ex.Message}");
        }
    }

    private static void Sanitise(OverlayConfig config)
    {
        config.Scale = Math.Clamp(config.Scale, 0.5f, 2.5f);
        config.Opacity = Math.Clamp(config.Opacity, 0.1f, 1f);
        config.SmoothingSeconds = Math.Clamp(config.SmoothingSeconds, 0f, 2f);

        config.LeftSlots = SanitiseSlots(config.LeftSlots, [ReadoutKind.Speed, ReadoutKind.Altitude]);
        config.RightSlots = SanitiseSlots(config.RightSlots, [ReadoutKind.GForce]);

        if (string.IsNullOrWhiteSpace(config.MissionName))
        {
            config.MissionName = null;
        }

        config.Windows ??= [];
        config.Windows.RemoveAll(static w => w is null || string.IsNullOrEmpty(w.Id));

        config.MissionEpochs ??= [];
    }

    private static ReadoutKind[] SanitiseSlots(ReadoutKind[]? slots, ReadoutKind[] fallback)
    {
        if (slots is null || slots.Length == 0)
        {
            return fallback;
        }

        int count = Math.Min(slots.Length, OverlayConfig.MaxSlotsPerSide);
        ReadoutKind[] trimmed = new ReadoutKind[count];

        for (int i = 0; i < count; i++)
        {
            trimmed[i] = Enum.IsDefined(slots[i]) ? slots[i] : ReadoutKind.Speed;
        }

        return trimmed;
    }
}
