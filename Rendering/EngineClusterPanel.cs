using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class EngineClusterPanel : IOverlayPanel
{
    private const float PreferredSize = 150f;
    private const float ArcHalfSweep = MathF.PI * 0.75f;

    private const float EnvelopeMargin = 3f;
    private const float MinDotRadius = 3.5f;
    private const float NeighbourFillFraction = 0.92f;
    private const float SingleEngineRadius = 0.42f;
    private float _propellant;
    private bool _initialised;

    public string Id => "engine_cluster";

    public string DisplayName => "Engine Cluster";

    public bool IsVisible(in PanelContext context)
        => context.Config.ShowEngineDiagram && context.Snapshot.HasVehicle;

    public float2 Measure(in PanelContext context) => new(PreferredSize, PreferredSize);

    public void Reset()
    {
        _initialised = false;
        _propellant = 0f;
    }

    public void Draw(in PanelContext context, float2 origin, float2 size)
    {
        ImDrawListPtr drawList = context.DrawList;
        TelemetrySnapshot snapshot = context.Snapshot;
        float opacity = context.Opacity;
        float scale = context.Scale;

        UpdateSmoothing(snapshot, context.DeltaTime, context.Config);

        float2 center = origin + size * 0.5f;
        float outerRadius = MathF.Min(size.X, size.Y) * 0.5f;
        float arcRadius = outerRadius - 3f * scale;
        float arcThickness = MathF.Max(2f, 3f * scale);

        bool showArc = context.Config.ShowPropellants;
        float diagramRadius = showArc
            ? arcRadius - arcThickness - 6f * scale
            : outerRadius - 3f * scale;
        float plateRadius = diagramRadius + 4f * scale;
        Gfx.GaugePlate(drawList, center, plateRadius, opacity, rim: true, scale);

        if (showArc)
        {
            DrawPropellantArc(drawList, center, arcRadius, arcThickness, opacity);
        }
        DrawEngines(drawList, snapshot, center, diagramRadius, opacity, scale);
    }

    private void UpdateSmoothing(TelemetrySnapshot snapshot, double dt, OverlayConfig config)
    {
        float target = snapshot.PropellantFraction;

        if (!_initialised)
        {
            _propellant = target;
            _initialised = true;
            return;
        }

        double tau = Math.Max(config.SmoothingSeconds, 1e-4);
        double alpha = 1.0 - Math.Exp(-dt / tau);
        _propellant += (float)((target - _propellant) * alpha);
    }

    private void DrawPropellantArc(
        ImDrawListPtr drawList, float2 center, float radius, float thickness, float opacity)
    {
        float start = -ArcHalfSweep;
        float end = ArcHalfSweep;

        Gfx.Arc(drawList, center, radius, start, end,
            OverlayStyle.ArcTrack, thickness, opacity);

        float level = Math.Clamp(_propellant, 0f, 1f);
        if (level <= 0f)
        {
            return;
        }

        float fillEnd = start + (end - start) * level;

        uint fillColor = level <= 0.10f ? OverlayStyle.EngineStarved
            : level <= 0.25f ? OverlayStyle.Caution
            : OverlayStyle.ArcFill;

        Gfx.Arc(drawList, center, radius, start, fillEnd, fillColor, thickness, opacity);
    }

    private static void DrawEngines(
        ImDrawListPtr drawList,
        TelemetrySnapshot snapshot,
        float2 center,
        float radius,
        float opacity,
        float scale)
    {
        List<EngineSample> engines = snapshot.Engines;

        if (engines.Count == 0)
        {
            float labelSize = OverlayFonts.LabelSize * scale;
            float2 extent = Gfx.MeasureWithFont(OverlayFonts.Label, labelSize, "NO ENGINES".AsSpan());
            Gfx.TextCenteredFont(
                drawList, OverlayFonts.Label, labelSize,
                center.X, center.Y - extent.Y * 0.5f,
                OverlayStyle.TextDim, "NO ENGINES".AsSpan(), opacity);
            return;
        }

        float maxSizeFactor = 0f;
        for (int i = 0; i < engines.Count; i++)
        {
            maxSizeFactor = MathF.Max(maxSizeFactor, SizeFactorOf(engines[i]));
        }

        float normalised = ComputeNormalisedDotRadius(engines);
        float margin = EnvelopeMargin * scale;
        float plotRadius = MathF.Max((radius - margin) / (1f + normalised * maxSizeFactor), 1f);
        float baseDotRadius = MathF.Max(normalised * plotRadius, MinDotRadius * scale);

        for (int i = 0; i < engines.Count; i++)
        {
            EngineSample engine = engines[i];

            float2 pos = new(
                center.X + engine.DiagramX * plotRadius,
                center.Y - engine.DiagramY * plotRadius);

            uint color = OverlayStyle.ColorFor(engine.Status);
            float dotRadius = MathF.Max(baseDotRadius * SizeFactorOf(engine), 2.5f * scale);

            if (engine.IsBurning)
            {
                // Soft halo reads as "lit" at a glance.
                drawList.AddCircleFilled(in pos, dotRadius * 1.3f,
                    OverlayStyle.WithOpacity(color, opacity * 0.20f));
            }

            if (engine.Status is EngineStatus.Inactive or EngineStatus.Armed)
            {
                drawList.AddCircle(in pos, dotRadius,
                    OverlayStyle.WithOpacity(color, opacity), 0, MathF.Max(1f, 1.4f * scale));
            }
            else
            {
                drawList.AddCircleFilled(in pos, dotRadius, OverlayStyle.WithOpacity(color, opacity));
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

    private static float SizeFactorOf(EngineSample engine)
        => engine.SizeFactor > 0f ? engine.SizeFactor : 1f;
}
