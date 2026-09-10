using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSATelemetryOverlay.Rendering;

// Currently doesn't do anything but sit there and look pretty, at some point I may have it show manuever nodes as events.
public sealed class TimelinePanel : IOverlayPanel
{
    private const float PreferredWidth = 560f;
    private const float PreferredHeight = 12f;
    private const float Progress = 0.5f;
    public string Id => "timeline";
    public string DisplayName => "Timeline";
    public bool IsVisible(in PanelContext context)
        => context.Config.ShowTimeline && context.Snapshot.HasVehicle;

    public float2 Measure(in PanelContext context) => new(PreferredWidth, PreferredHeight);

    public void Reset()
    {
        // nothing yet
    }

    public void Draw(in PanelContext context, float2 origin, float2 size)
    {
        ImDrawListPtr drawList = context.DrawList;
        float opacity = context.Opacity;
        float scale = context.Scale;

        float trackY = origin.Y + size.Y * 0.5f;
        float left = origin.X;
        float right = origin.X + size.X;
        float split = left + size.X * Progress;

        float thickness = MathF.Max(1f, 1.4f * scale);

        float2 pastA = new(left, trackY);
        float2 pastB = new(split, trackY);
        drawList.AddLine(in pastA, in pastB,
            OverlayStyle.WithOpacity(OverlayStyle.TimelinePast, opacity), thickness);

        float2 futureA = new(split, trackY);
        float2 futureB = new(right, trackY);
        drawList.AddLine(in futureA, in futureB,
            OverlayStyle.WithOpacity(OverlayStyle.TimelineFuture, opacity), thickness);
    }
}
