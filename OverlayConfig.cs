using Brutal.ImGuiApi;
using KSATelemetryOverlay.Rendering;

namespace KSATelemetryOverlay;
public sealed class OverlayConfig
{
    // visibility
    public bool Enabled = false;
    public bool ShowTelemetryBar = true;
    public bool ShowEngineDiagram = true;
    public bool ShowPropellants = true;
    public bool ShowMissionClock = true;
    public bool ShowTimeline = true;
    public bool ShowBackdrop = true;
    public bool ShowNotifications = true;
    public bool ShowStatusWhenIdle = false;
    public bool HideOnRails = false;

    // behaviour
    public ImGuiKey ToggleKey = ImGuiKey.KeypadDecimal;
    public ImGuiKey SettingsKey = ImGuiKey.KeypadDivide;
    public bool ReplaceFlightUi = true;
    public string? MissionName = null;

    // presentation
    public float Scale = 0.83f;
    public float Opacity = 1f;
    public float SmoothingSeconds = 0.12f;

    // readout slots
    public ReadoutKind[] LeftSlots = [ReadoutKind.Speed, ReadoutKind.Altitude];
    public ReadoutKind[] RightSlots = [ReadoutKind.GForce];

    // standalone windows
    public List<OverlayWindowState> Windows = [];

    /// <summary>
    /// Detected liftoff times keyed by mission, so T+ survives a restart. Written
    /// as strings because the key is an Int128 nanosecond stamp.
    /// </summary>
    public Dictionary<string, double> MissionEpochs = [];
    public int Revision;
    public void MarkStructuralChange() => Revision++;
    public const int MaxSlotsPerSide = 4;
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
