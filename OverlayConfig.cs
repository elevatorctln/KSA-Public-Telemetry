using System.Text.Json.Serialization;
using Brutal.ImGuiApi;
using KSATelemetryOverlay.Rendering;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay;
public sealed class OverlayConfig
{
    // visibility
    public bool Enabled = false;
    public bool ShowTelemetryBar = true;
    public bool ShowEngineDiagram = true;
    public float EngineDiagramRotation = 0f;
    public float SpeedArcFullScale = 7800f;
    public float AltitudeArcFullScaleKm = 100f;
    public float GForceArcFullScale = 6f;
    public bool ShowPropellants = true;
    public bool ShowMissionClock = true;
    public bool ShowTimeline = true;
    public bool ShowBackdrop = true;
    public bool ShowNotifications = true;
    public bool ShowStatusWhenIdle = false;
    public bool HideOnRails = false;
    public SpeedReference SpeedReference = SpeedReference.Surface;
    public float CountdownSeconds = 10f;
    public bool TerrainRelativeAltitude = false;

    // behaviour
    public ImGuiKey ToggleKey = ImGuiKey.KeypadDecimal;
    public ImGuiKey SettingsKey = ImGuiKey.KeypadDivide;
    public ImGuiKey CountdownKey = ImGuiKey.KeypadMultiply;
    public bool ReplaceFlightUi = true;
    public bool ShowWhenGameUiHidden = true;
    public string? MissionName = null;

    // presentation
    public float Scale = 0.83f;
    public float Opacity = 1f;
    public float SmoothingSeconds = 0.12f;
    public float FreezeAtToleranceFraction = 0.95f;
    public float TimelineWindowSeconds = 240f;

    // readout slots
    public ReadoutKind[] LeftSlots = [ReadoutKind.Speed, ReadoutKind.Altitude];
    public ReadoutKind[] RightSlots = [ReadoutKind.GForce];

    // standalone windows
    public List<OverlayWindowState> Windows = [];
    public List<MissionEventKind> MutedNotifications = [];
    public Dictionary<string, SavedOrigin> MissionOrigins = [];
    public Dictionary<string, double> MissionEpochs = [];

    [JsonIgnore]
    public int Revision;
    public void MarkStructuralChange() => Revision++;
    public const int MaxSlotsPerSide = 4;
    public const float MinScale = 0.5f, MaxScale = 2.5f;
    public const float MinOpacity = 0.1f, MaxOpacity = 1f;
    public const float MinSmoothing = 0f, MaxSmoothing = 1f;
    public const float MinTimelineWindow = 60f, MaxTimelineWindow = 1800f;
    public const float MinFreezeTolerance = 0.1f, MaxFreezeTolerance = 1f;
    public const float MinEngineRotation = 0f, MaxEngineRotation = 360f;
    public const float MinCountdown = 1f, MaxCountdown = 600f;
    public const float MinSpeedArc = 100f, MaxSpeedArc = 12000f;
    public const float MinAltitudeArc = 0f, MaxAltitudeArc = 2000f;
    public const float MinGForceArc = 1f, MaxGForceArc = 20f;
}

public sealed class OverlayWindowState
{
    public string Id = string.Empty;
    public bool Open;
    public float X;
    public float Y;
    public float Width;
    public float Height;
}
