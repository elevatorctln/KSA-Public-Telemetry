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
    double deltaTime,
    IntroPhases intro,
    float2 viewportPos,
    float2 viewportSize,
    IntroAnimator? animator = null)
{
    public readonly ImDrawListPtr DrawList = drawList;
    public readonly TelemetrySnapshot Snapshot = snapshot;
    public readonly OverlayConfig Config = config;
    public readonly double DeltaTime = deltaTime;
    public readonly IntroPhases Intro = intro;
    public readonly float2 ViewportPos = viewportPos;
    public readonly float2 ViewportSize = viewportSize;
    private readonly IntroAnimator? _animator = animator;
    public float Scale => Config.Scale;
    public float Opacity => Config.Opacity;
    public float BackdropOpacity => Opacity * Intro.Backdrop;
    public float GaugeOpacity => Opacity * Intro.Gauges;
    public float ReadoutOpacity => Opacity * Intro.Readouts;

    public IntroPhases IntroFor(float centerX, float elementWidth)
    {
        if (elementWidth <= 0f)
        {
            return Intro;
        }

        float middle = ViewportPos.X + ViewportSize.X * 0.5f;

        float fromEdge = centerX < middle
            ? centerX - ViewportPos.X
            : ViewportPos.X + ViewportSize.X - centerX;

        float steps = MathF.Max(fromEdge / elementWidth - 0.5f, 0f);

        float delay = MathF.Min(steps * Tuning.GaugeStaggerStep, MathF.Max(Tuning.GaugeStaggerMax, 0f));

        return _animator is null ? Intro : _animator.PhasesDelayedBy(delay);
    }
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
