using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;
public sealed class TimelinePanel : IOverlayPanel
{
    private const float PreferredWidth = 1000f;
    private const float PreferredHeight = 56f;
    private const double WindowSeconds = 240.0;
    private const float NowFraction = 0.5f;
    private const float LabelOffset = 7f;
    private const float MinLabelGap = 6f;
    private const float DotRadius = 3.5f;
    private const float EndFadeFraction = 0.16f;
    private const float MinLabelAlpha = 0.35f;
    private readonly List<MissionEvent> _visible = new(16);

    private static readonly IComparer<MissionEvent> TimeOrder = new MissionTimeComparer();

    private sealed class MissionTimeComparer : IComparer<MissionEvent>
    {
        public int Compare(MissionEvent a, MissionEvent b)
            => a.MissionTime.CompareTo(b.MissionTime);
    }

    public string Id => "timeline";

    public string DisplayName => "Timeline";

    public bool IsVisible(in PanelContext context)
        => context.Config.ShowTimeline && context.Snapshot.HasVehicle;

    public float2 Measure(in PanelContext context) => new(PreferredWidth, PreferredHeight);

    public void Reset() => _visible.Clear();

    public void Draw(in PanelContext context, float2 origin, float2 size)
    {
        ImDrawListPtr drawList = context.DrawList;
        float opacity = context.Opacity;
        float scale = context.Scale;

        double now = context.Snapshot.MissionElapsedSeconds;
        double halfWindow = WindowSeconds * 0.5;

        float trackY = origin.Y + size.Y * 0.5f;
        float left = origin.X;
        float right = origin.X + size.X;
        float nowX = left + size.X * NowFraction;

        float thickness = MathF.Max(1f, 1.4f * scale);

        Gfx.FadedTrack(
            drawList, left, right, nowX, trackY, thickness,
            OverlayStyle.TimelinePast, OverlayStyle.TimelineFuture,
            size.X * EndFadeFraction, opacity);

        float2 marker = new(nowX, trackY);
        drawList.AddCircleFilled(in marker, MathF.Max(3f, 3.5f * scale),
            OverlayStyle.WithOpacity(OverlayStyle.TimelinePast, opacity), 16);

        CollectVisible(context.Snapshot, now, halfWindow);

        if (_visible.Count == 0)
        {
            return;
        }

        DrawEvents(drawList, size, left, trackY, now, halfWindow, opacity, scale);
    }

    private void CollectVisible(TelemetrySnapshot snapshot, double now, double halfWindow)
    {
        _visible.Clear();

        MissionEventLog? log = snapshot.Events;
        if (log is null)
        {
            return;
        }

        Add(log.Recorded, now, halfWindow);
        Add(log.Predicted, now, halfWindow);

        _visible.Sort(TimeOrder);
    }

    private void Add(IReadOnlyList<MissionEvent> source, double now, double halfWindow)
    {
        for (int i = 0; i < source.Count; i++)
        {
            if (Math.Abs(source[i].MissionTime - now) <= halfWindow)
            {
                _visible.Add(source[i]);
            }
        }
    }

    private void DrawEvents(
        ImDrawListPtr drawList,
        float2 size,
        float left,
        float trackY,
        double now,
        double halfWindow,
        float opacity,
        float scale)
    {
        float labelSize = OverlayFonts.LabelSize * scale;
        float labelOffset = LabelOffset * scale;
        float minGap = MinLabelGap * scale;

        Span<float> rowCursor = [float.NegativeInfinity, float.NegativeInfinity];

        int row = 0;

        for (int i = 0; i < _visible.Count; i++)
        {
            MissionEvent e = _visible[i];

            double delta = e.MissionTime - now;
            float t = (float)((delta + halfWindow) / (halfWindow * 2.0));
            float x = left + size.X * t;

            float proximity = 1f - (float)(Math.Abs(delta) / halfWindow);
            float fadeWidth = size.X * EndFadeFraction;
            float endFade = fadeWidth > 0f
                ? Math.Clamp(MathF.Min(x - left, left + size.X - x) / fadeWidth, 0f, 1f)
                : 1f;

            float alpha = opacity * MathF.Max(proximity, MinLabelAlpha) * endFade;

            if (alpha <= 0.01f)
            {
                continue;
            }

            uint trackColor = delta <= 0.0 ? OverlayStyle.TimelinePast : OverlayStyle.TimelineFuture;

            float2 dot = new(x, trackY);
            drawList.AddCircleFilled(in dot, MathF.Max(2f, DotRadius * scale),
                OverlayStyle.WithOpacity(trackColor, alpha), 12);

            float2 extent = Gfx.MeasureWithFont(OverlayFonts.Label, labelSize, e.Label);
            float labelLeft = x - extent.X * 0.5f;

            int chosen = -1;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int candidate = (row + attempt) % 2;
                if (labelLeft >= rowCursor[candidate] + minGap)
                {
                    chosen = candidate;
                    break;
                }
            }

            if (chosen < 0)
            {
                continue;
            }

            rowCursor[chosen] = labelLeft + extent.X;
            row = (chosen + 1) % 2;

            float labelY = chosen == 0
                ? trackY - labelOffset - extent.Y
                : trackY + labelOffset;

            uint color = e.IsPrediction ? OverlayStyle.TextDim : OverlayStyle.TextPrimary;

            Gfx.TextCenteredFont(
                drawList, OverlayFonts.Label, labelSize,
                x, labelY, color, e.Label, alpha);
        }
    }
}
