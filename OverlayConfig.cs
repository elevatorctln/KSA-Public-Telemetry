using System.Text.Json.Serialization;
using Brutal.ImGuiApi;
using KSATelemetryOverlay.Rendering;

namespace KSATelemetryOverlay;
public sealed class OverlayConfig
{
    // visibility
    public bool Enabled = false;
    public bool ShowTelemetryBar = true;
    public bool ShowEngineDiagram = true;

    /// <summary>
    /// Spins the engine dots inside the diagram, in degrees anticlockwise. Purely a
    /// drawing transform: rotation preserves the distances between dots, so neither
    /// the packing radius nor the ring grouping of the pop-in is affected.
    /// </summary>
    public float EngineDiagramRotation = 0f;
    public bool ShowPropellants = true;
    public bool ShowMissionClock = true;
    public bool ShowTimeline = true;
    public bool ShowBackdrop = true;
    public bool ShowNotifications = true;
    public bool ShowStatusWhenIdle = false;
    public bool HideOnRails = false;
    public bool TerrainRelativeAltitude = false;

    // behaviour
    public ImGuiKey ToggleKey = ImGuiKey.KeypadDecimal;
    public ImGuiKey SettingsKey = ImGuiKey.KeypadDivide;
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
