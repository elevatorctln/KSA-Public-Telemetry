using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;
public sealed class NotificationPanel : IOverlayPanel
{
    private const double FadeIn = 0.2;
    private const double Hold = 4.5;
    private const double FadeOut = 0.8;
    private const double Lifetime = FadeIn + Hold + FadeOut;
    private const int MaxVisible = 3;
    private const float PadX = 14f;
    private const float PadY = 7f;
    private const float LabelGap = 8f;
    private const float EntryGap = 1f;
    private const float Notch = 16f;
    private const float LabelSizeMultiplier = 15f / 11f;
    private const float ExplainerSizeMultiplier = 14f / 16f;

    private static float LabelFontSize => OverlayFonts.LabelSize * LabelSizeMultiplier;
    private static float ExplainerFontSize => OverlayFonts.BodySize * ExplainerSizeMultiplier;

    private sealed class Entry(MissionEvent missionEvent)
    {
        public readonly MissionEvent Event = missionEvent;
        public double Age;
    }

    private readonly List<Entry> _entries = [];
    private int _seenCount;
    private int _seenGeneration = -1;
    private readonly List<float> _entryHeights = [];

    private float _measuredWidth;

    public string Id => "notifications";

    public string DisplayName => "Event Notifications";

    public void Reset()
    {
        _entries.Clear();
        _entryHeights.Clear();
        _seenCount = 0;
        _seenGeneration = -1;
    }
    public void Update(TelemetrySnapshot snapshot, double dt)
    {
        MissionEventLog? log = snapshot.Events;

        if (log is null)
        {
            Reset();
            return;
        }

        if (log.Generation != _seenGeneration)
        {
            _seenGeneration = log.Generation;
            _seenCount = log.Recorded.Count;
            _entries.Clear();
        }

        IReadOnlyList<MissionEvent> recorded = log.Recorded;

        if (recorded.Count < _seenCount)
        {
            _seenCount = recorded.Count;
        }

        for (int i = _seenCount; i < recorded.Count; i++)
        {
            if (recorded[i].DeservesCallout)
            {
                _entries.Add(new Entry(recorded[i]));
            }
        }

        _seenCount = recorded.Count;

        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            _entries[i].Age += dt;

            if (_entries[i].Age >= Lifetime)
            {
                _entries.RemoveAt(i);
            }
        }

        while (_entries.Count > MaxVisible)
        {
            _entries.RemoveAt(0);
        }
    }

    public bool IsVisible(in PanelContext context)
        => context.Config.ShowNotifications && _entries.Count > 0;

    public float2 Measure(in PanelContext context)
    {
        _entryHeights.Clear();
        _measuredWidth = 0f;

        float total = 0f;

        for (int i = 0; i < _entries.Count; i++)
        {
            MissionEvent e = _entries[i].Event;

            float2 label = Gfx.MeasureWithFont(OverlayFonts.Label, LabelFontSize, e.Label);
            float2 explainer = Gfx.MeasureWithFont(OverlayFonts.Body, ExplainerFontSize, e.Explainer);

            float width = PadX * 2f + Notch + label.X;
            if (!e.Explainer.IsEmpty)
            {
                width += LabelGap + explainer.X;
            }

            float height = MathF.Max(label.Y, explainer.Y) + PadY * 2f;

            _measuredWidth = MathF.Max(_measuredWidth, width);
            _entryHeights.Add(height);

            total += height;
            if (i > 0)
            {
                total += EntryGap;
            }
        }

        return new float2(_measuredWidth, total);
    }

    public void Draw(in PanelContext context, float2 origin, float2 size)
    {
        if (_entryHeights.Count != _entries.Count)
        {
            return;
        }

        ImDrawListPtr drawList = context.DrawList;
        float scale = context.Scale;
        float y = origin.Y;

        for (int i = 0; i < _entries.Count; i++)
        {
            Entry entry = _entries[i];
            float height = _entryHeights[i] * scale;
            float alpha = context.Opacity * FadeFactor(entry.Age);

            DrawBanner(drawList, entry.Event, new float2(origin.X, y), size.X, height, alpha, scale);

            y += height + EntryGap * scale;
        }
    }

    private static float FadeFactor(double age)
    {
        if (age < FadeIn)
        {
            return (float)(age / FadeIn);
        }

        double intoFade = age - FadeIn - Hold;
        if (intoFade <= 0.0)
        {
            return 1f;
        }

        return (float)Math.Clamp(1.0 - intoFade / FadeOut, 0.0, 1.0);
    }

    private static void DrawBanner(
        ImDrawListPtr drawList,
        MissionEvent missionEvent,
        float2 origin,
        float width,
        float height,
        float alpha,
        float scale)
    {
        float notch = Notch * scale;
        float right = origin.X + width;
        float bottom = origin.Y + height;

        Span<float2> shape =
        [
            new(origin.X, origin.Y),
            new(right, origin.Y),
            new(right - notch, bottom),
            new(origin.X, bottom),
        ];

        drawList.AddConvexPolyFilled(
            shape, OverlayStyle.WithOpacity(OverlayStyle.PanelBackground, MathF.Min(alpha * 1.6f, 1f)));

        float labelSize = LabelFontSize * scale;
        float explainerSize = ExplainerFontSize * scale;

        float2 labelExtent = Gfx.MeasureWithFont(OverlayFonts.Label, labelSize, missionEvent.Label);
        float2 explainerExtent =
            Gfx.MeasureWithFont(OverlayFonts.Body, explainerSize, missionEvent.Explainer);

        float rowHeight = MathF.Max(labelExtent.Y, explainerExtent.Y);
        float textTop = origin.Y + (height - rowHeight) * 0.5f;
        float x = origin.X + PadX * scale;

        Gfx.TextFont(
            drawList, OverlayFonts.Label, labelSize,
            new float2(x, textTop + (rowHeight - labelExtent.Y) * 0.5f),
            OverlayStyle.TextPrimary, missionEvent.Label, alpha);

        if (missionEvent.Explainer.IsEmpty)
        {
            return;
        }

        x += labelExtent.X + LabelGap * scale;

        Gfx.TextFont(
            drawList, OverlayFonts.Body, explainerSize,
            new float2(x, textTop + (rowHeight - explainerExtent.Y) * 0.5f),
            OverlayStyle.TextMuted, missionEvent.Explainer, alpha);
    }
}
