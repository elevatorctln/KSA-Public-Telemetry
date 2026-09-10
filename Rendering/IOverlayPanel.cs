using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;
public enum PanelAnchor : byte
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public readonly struct PanelContext(
    ImDrawListPtr drawList,
    TelemetrySnapshot snapshot,
    OverlayConfig config,
    double deltaTime)
{
    public readonly ImDrawListPtr DrawList = drawList;
    public readonly TelemetrySnapshot Snapshot = snapshot;
    public readonly OverlayConfig Config = config;
    public readonly double DeltaTime = deltaTime;

    public float Scale => Config.Scale;
    public float Opacity => Config.Opacity;
}

public interface IOverlayPanel
{
    string Id { get; }
    string DisplayName { get; }
    bool IsVisible(in PanelContext context);
    float2 Measure(in PanelContext context);
    void Draw(in PanelContext context, float2 origin, float2 size);
    void Reset();
}
