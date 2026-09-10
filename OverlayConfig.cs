using Brutal.ImGuiApi;

namespace KSATelemetryOverlay;

// TODO: make this a proper config file that can be saved and loaded instead of these placeholder hardcoded values. This is obviously not a real way to store settings.
public sealed class OverlayConfig
{
    public bool Enabled = true;
    public ImGuiKey ToggleKey = ImGuiKey.F8;
    public bool ShowTelemetryBar = true;
    public bool ShowEngineDiagram = true;
    public bool ShowPropellants = true;
    public bool ShowMissionClock = true;
    public bool ShowTimeline = true;
    public bool ShowBackdrop = true;
    public string? MissionName = null;
    public bool HideOnRails = false;
    public bool ShowStatusWhenIdle = true;
    public float Scale = 1f;
    public float Opacity = 0.92f;
    public float SmoothingSeconds = 0.12f;
}
