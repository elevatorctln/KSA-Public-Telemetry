using System.Text.Json;
using System.Text.Json.Nodes;
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

            JsonObject? root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            }) as JsonObject;

            if (LoadSettings(root?["settings"]) is { } loaded)
            {
                config = loaded;
                Sanitise(config);
            }

            OverlayPalette.ApplyOverrides(LoadSection<Dictionary<string, string>>(root?["colors"], "colors"));
            Rendering.Tuning.ApplyOverrides(LoadSection<Dictionary<string, double>>(root?["tuning"], "tuning"));
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

    private static T? LoadSection<T>(JsonNode? node, string name) where T : class
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.Deserialize<T>(_options);
        }
        catch (JsonException ex)
        {
            Console.WriteLine(LogPrefix + $"ignoring the '{name}' section of the config: {ex.Message}");
            return null;
        }
    }

    private static readonly string[] _fragileSettings =
        ["ToggleKey", "SettingsKey", "LeftSlots", "RightSlots", "Windows", "MissionEpochs",
         "MutedNotifications", "SpeedReference", "CountdownKey", "MissionOrigins"];

    private static OverlayConfig? LoadSettings(JsonNode? node)
    {
        if (node is not JsonObject settings)
        {
            return null;
        }

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return settings.Deserialize<OverlayConfig>(_options);
            }
            catch (JsonException ex)
            {
                if (attempt >= _fragileSettings.Length)
                {
                    throw;
                }

                string dropped = _fragileSettings[attempt];
                Console.WriteLine(LogPrefix + $"could not read '{dropped}' from the config, using its default: {ex.Message}");
                settings.Remove(dropped);
            }
        }
    }

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
        config.Scale = Math.Clamp(config.Scale, OverlayConfig.MinScale, OverlayConfig.MaxScale);
        config.Opacity = Math.Clamp(config.Opacity, OverlayConfig.MinOpacity, OverlayConfig.MaxOpacity);
        config.SmoothingSeconds = Math.Clamp(
            config.SmoothingSeconds, OverlayConfig.MinSmoothing, OverlayConfig.MaxSmoothing);
        config.TimelineWindowSeconds = Math.Clamp(
            config.TimelineWindowSeconds, OverlayConfig.MinTimelineWindow, OverlayConfig.MaxTimelineWindow);
        config.CountdownSeconds = Math.Clamp(
            config.CountdownSeconds, OverlayConfig.MinCountdown, OverlayConfig.MaxCountdown);
        config.SpeedArcFullScale = Math.Clamp(
            config.SpeedArcFullScale, OverlayConfig.MinSpeedArc, OverlayConfig.MaxSpeedArc);
        config.AltitudeArcFullScaleKm = Math.Clamp(
            config.AltitudeArcFullScaleKm, OverlayConfig.MinAltitudeArc, OverlayConfig.MaxAltitudeArc);
        config.GForceArcFullScale = Math.Clamp(
            config.GForceArcFullScale, OverlayConfig.MinGForceArc, OverlayConfig.MaxGForceArc);
        config.EngineDiagramRotation = Math.Clamp(
            config.EngineDiagramRotation,
            OverlayConfig.MinEngineRotation,
            OverlayConfig.MaxEngineRotation);
        config.FreezeAtToleranceFraction = Math.Clamp(
            config.FreezeAtToleranceFraction,
            OverlayConfig.MinFreezeTolerance,
            OverlayConfig.MaxFreezeTolerance);

        config.LeftSlots = SanitiseSlots(config.LeftSlots, [ReadoutKind.Speed, ReadoutKind.Altitude]);
        config.RightSlots = SanitiseSlots(config.RightSlots, [ReadoutKind.GForce]);

        if (string.IsNullOrWhiteSpace(config.MissionName))
        {
            config.MissionName = null;
        }

        config.Windows ??= [];
        config.Windows.RemoveAll(static w => w is null || string.IsNullOrEmpty(w.Id));

        config.MissionEpochs ??= [];
        config.MissionOrigins ??= [];
        config.MutedNotifications ??= [];
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
