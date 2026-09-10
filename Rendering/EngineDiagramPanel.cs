using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class EngineDiagramPanel
{
    private const float PanelPadding = 12f;

    public float Draw(ImDrawListPtr drawList, TelemetrySnapshot snapshot, float2 topLeft, float width, OverlayConfig config)
    {
        float opacity = config.Opacity;

        float lineHeight = ImGui.GetTextLineHeight();
        float headerHeight = lineHeight + MathF.Round(lineHeight * 0.5f);

        float diagramSize = width - PanelPadding * 2f;
        float height = PanelPadding + headerHeight + diagramSize + PanelPadding;

        float2 min = topLeft;
        float2 max = topLeft + new float2(width, height);
        Gfx.Panel(drawList, min, max, opacity);

        Span<char> buffer = stackalloc char[64];
        float headerY = min.Y + PanelPadding;
        Gfx.Text(drawList, new float2(min.X + PanelPadding, headerY), OverlayStyle.TextMuted, "ENGINES".AsSpan(), opacity);

        int written = 0;
        if (snapshot.BurningEngineCount.TryFormat(buffer, out int w1))
        {
            written = w1;
            " / ".AsSpan().CopyTo(buffer[written..]);
            written += 3;
            if (snapshot.TotalEngineCount.TryFormat(buffer[written..], out int w2))
            {
                written += w2;
            }
        }

        uint countColor = snapshot.BurningEngineCount > 0 ? OverlayStyle.EngineNominal : OverlayStyle.TextMuted;
        Gfx.TextRight(drawList, max.X - PanelPadding, headerY, countColor, buffer[..written], opacity);

        float2 center = new(
            min.X + width * 0.5f,
            min.Y + PanelPadding + headerHeight + diagramSize * 0.5f);
        float radius = diagramSize * 0.5f;

        DrawEngines(drawList, snapshot, center, radius, opacity);

        return height;
    }

    private static void DrawEngines(
        ImDrawListPtr drawList, TelemetrySnapshot snapshot, float2 center, float radius, float opacity)
    {
        List<EngineSample> engines = snapshot.Engines;
        if (engines.Count == 0)
        {
            Gfx.TextCentered(drawList, center.X, center.Y - 6f, OverlayStyle.TextMuted, "NO ENGINES".AsSpan(), opacity);
            return;
        }

        drawList.AddCircle(in center, radius, OverlayStyle.WithOpacity(OverlayStyle.Hairline, opacity), 48, 1f);

        float maxSizeFactor = 0f;
        for (int i = 0; i < engines.Count; i++)
        {
            maxSizeFactor = MathF.Max(maxSizeFactor, SizeFactorOf(engines[i]));
        }

        float normalisedDotRadius = ComputeNormalisedDotRadius(engines) * maxSizeFactor;
        float plotRadius = MathF.Max((radius - EnvelopeMargin) / (1f + normalisedDotRadius), 1f);
        float baseDotRadius = MathF.Max(ComputeNormalisedDotRadius(engines) * plotRadius, MinDotRadius);

        for (int i = 0; i < engines.Count; i++)
        {
            EngineSample engine = engines[i];

            float2 pos = new(
                center.X + engine.DiagramX * plotRadius,
                center.Y - engine.DiagramY * plotRadius);

            uint color = OverlayStyle.ColorFor(engine.Status);

            float dotRadius = MathF.Max(baseDotRadius * SizeFactorOf(engine), 2.5f);

            if (engine.IsBurning)
            {
                drawList.AddCircleFilled(in pos, dotRadius * 1.25f, OverlayStyle.WithOpacity(color, opacity * 0.22f));
            }

            drawList.AddCircleFilled(in pos, dotRadius, OverlayStyle.WithOpacity(color, opacity));
            drawList.AddCircle(in pos, dotRadius, OverlayStyle.WithOpacity(OverlayStyle.EngineOutline, opacity), 0, 1.5f);

            if (engine.Status == EngineStatus.Throttled && dotRadius >= 5f)
            {
                DrawThrottleWedge(drawList, pos, dotRadius, engine.Throttle, opacity);
            }
        }
    }

    private static float ComputeNormalisedDotRadius(List<EngineSample> engines)
    {
        int count = engines.Count;

        if (count == 1)
        {
            return SingleEngineRadius;
        }

        float minSeparation = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            for (int j = i + 1; j < count; j++)
            {
                float dx = engines[i].DiagramX - engines[j].DiagramX;
                float dy = engines[i].DiagramY - engines[j].DiagramY;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d > 1e-4f && d < minSeparation)
                {
                    minSeparation = d;
                }
            }
        }

        if (minSeparation == float.MaxValue)
        {
            return MathF.Min(SingleEngineRadius, 1f / MathF.Sqrt(count));
        }

        return MathF.Min(minSeparation * 0.5f * NeighbourFillFraction, SingleEngineRadius);
    }

    private const float SingleEngineRadius = 0.42f;
    private const float EnvelopeMargin = 3f;
    private const float MinDotRadius = 3.5f;
    private const float NeighbourFillFraction = 0.92f;
    private static float SizeFactorOf(EngineSample engine)
        => engine.SizeFactor > 0f ? engine.SizeFactor : 1f;
    private static void DrawThrottleWedge(ImDrawListPtr drawList, float2 center, float radius, float throttle, float opacity)
    {
        float clamped = Math.Clamp(throttle, 0f, 1f);
        if (clamped <= 0f)
        {
            return;
        }

        float2 min = new(center.X - radius, center.Y + radius - radius * 2f * clamped);
        float2 max = new(center.X + radius, center.Y + radius);
        drawList.AddRectFilled(in min, in max, OverlayStyle.WithOpacity(OverlayStyle.EngineNominal, opacity * 0.5f));
    }
}
