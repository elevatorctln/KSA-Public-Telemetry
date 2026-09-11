using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class EngineClusterPanel : IOverlayPanel
{
    private const float PreferredSize = 145f;
    private const float ArcHalfSweep = MathF.PI * 0.75f;
    private float _propellant;
    private bool _initialised;
    private struct DotState
    {
        public bool Active;
        public EngineStatus Shown;
        public EngineStatus Pending;
        public double Delay;
        public uint FromColor;
        public float Fade;
    }

    private DotState[] _dots = [];
    private readonly Random _jitter = new();
    private enum SwapPhase : byte { Idle, Out, In }
    private SwapPhase _swap = SwapPhase.Idle;
    private float _swapT = 1f;

    private sealed class DotSet
    {
        public readonly List<float2> Offsets = [];
        public readonly List<float> SizeFactors = [];
        public readonly List<uint> Colors = [];
        public readonly List<bool> Burning = [];

        public float Normalised;
        public float MaxSizeFactor;
        public float[] RingStarts = [];

        public int Count => Offsets.Count;

        public void Clear()
        {
            Offsets.Clear();
            SizeFactors.Clear();
            Colors.Clear();
            Burning.Clear();
        }

        public void CopyFrom(DotSet other)
        {
            Clear();
            Offsets.AddRange(other.Offsets);
            SizeFactors.AddRange(other.SizeFactors);
            Colors.AddRange(other.Colors);
            Burning.AddRange(other.Burning);
            Normalised = other.Normalised;
            MaxSizeFactor = other.MaxSizeFactor;
            RingStarts = other.RingStarts;
        }
    }

    private readonly DotSet _live = new();
    private readonly DotSet _outgoing = new();

    public string Id => "engine_cluster";

    public string DisplayName => "Engine Cluster";

    public bool IsVisible(in PanelContext context)
        => context.Config.ShowEngineDiagram && context.Snapshot.HasVehicle;

    public float2 Measure(in PanelContext context) => new(PreferredSize, PreferredSize);

    public void Reset()
    {
        _initialised = false;
        _propellant = 0f;
        _dots = [];
        _live.Clear();
        _outgoing.Clear();
        _swap = SwapPhase.Idle;
        _swapT = 1f;
    }

    public void Draw(in PanelContext context, float2 origin, float2 size)
    {
        ImDrawListPtr drawList = context.DrawList;
        TelemetrySnapshot snapshot = context.Snapshot;
        float scale = context.Scale;

        UpdateSmoothing(snapshot, context.DeltaTime, context.Config);
        UpdateCluster(snapshot, context.DeltaTime);

        float2 center = origin + size * 0.5f;

        IntroPhases intro = context.IntroFor(center.X, size.X);
        float gaugeOpacity = context.Opacity * intro.Gauges;
        float dotOpacity = context.Opacity * intro.Readouts;
        float outerRadius = MathF.Min(size.X, size.Y) * 0.5f;
        float arcRadius = outerRadius - 3f * scale;
        float arcThickness = MathF.Max(2f, 3f * scale);

        bool showArc = context.Config.ShowPropellants;
        float diagramRadius = showArc
            ? arcRadius - arcThickness - 6f * scale
            : outerRadius - 3f * scale;
        float plateRadius = diagramRadius + 4f * scale;
        Gfx.GaugePlate(drawList, center, plateRadius, gaugeOpacity, rim: true, scale, intro.Rim);

        if (showArc)
        {
            DrawPropellantArc(
                drawList, center, arcRadius, arcThickness, gaugeOpacity, intro.ArcSweep);
        }

        DrawEngines(drawList, center, diagramRadius, dotOpacity, scale, intro.Readouts);
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
        ImDrawListPtr drawList,
        float2 center,
        float radius,
        float thickness,
        float opacity,
        float fillPhase)
    {
        float start = -ArcHalfSweep;
        float end = ArcHalfSweep;

        if (fillPhase <= 0f)
        {
            return;
        }

        Gfx.Arc(drawList, center, radius, start, start + (end - start) * fillPhase,
            OverlayStyle.ArcTrack, thickness, opacity);

        float level = Math.Clamp(_propellant, 0f, 1f) * fillPhase;
        if (level <= 0f)
        {
            return;
        }

        float fillEnd = start + (end - start) * level;

        // not using this anymore, but I want to leave it in for now
        uint fillColor = level <= 0.10f ? OverlayStyle.ArcFill
            : level <= 0.25f ? OverlayStyle.ArcFill
            : OverlayStyle.ArcFill;

        Gfx.Arc(drawList, center, radius, start, fillEnd, fillColor, thickness, opacity);
    }

    private void UpdateDotStates(List<EngineSample> engines, double dt)
    {
        for (int i = 0; i < engines.Count; i++)
        {
            EngineStatus status = engines[i].Status;
            ref DotState dot = ref _dots[i];

            if (!dot.Active)
            {
                dot.Active = true;
                dot.Shown = status;
                dot.Pending = status;
                dot.Delay = 0.0;
                dot.FromColor = OverlayStyle.ColorFor(status);
                dot.Fade = 1f;
                continue;
            }

            if (status != dot.Pending)
            {
                dot.Pending = status;
                dot.Delay = _jitter.NextDouble() * Tuning.StateJitterSeconds;
            }

            if (dot.Pending != dot.Shown)
            {
                dot.Delay -= dt;

                if (dot.Delay <= 0.0)
                {
                    dot.FromColor = CurrentColor(in dot);
                    dot.Shown = dot.Pending;
                    dot.Fade = 0f;
                }
            }

            if (dot.Fade < 1f)
            {
                dot.Fade = Math.Clamp(dot.Fade + (float)(dt / Tuning.StateFadeSeconds), 0f, 1f);
            }
        }
    }

    private static uint CurrentColor(ref readonly DotState dot)
        => OverlayStyle.Lerp(dot.FromColor, OverlayStyle.ColorFor(dot.Shown), dot.Fade);

    private void UpdateCluster(TelemetrySnapshot snapshot, double dt)
    {
        List<EngineSample> engines = snapshot.Engines;

        if (engines.Count != _dots.Length)
        {
            if (_live.Count > 0)
            {
                _outgoing.CopyFrom(_live);
                _swap = SwapPhase.Out;
                _swapT = 0f;
            }

            _dots = new DotState[engines.Count];

            float maxSizeFactor = 0f;
            for (int i = 0; i < engines.Count; i++)
            {
                maxSizeFactor = MathF.Max(maxSizeFactor, SizeFactorOf(engines[i]));
            }

            _live.Normalised = engines.Count > 0 ? ComputeNormalisedDotRadius(engines) : 0f;
            _live.MaxSizeFactor = maxSizeFactor;
            _live.RingStarts = ComputeRingStarts(engines, _live.Normalised);
        }

        AdvanceSwap(dt);
        UpdateDotStates(engines, dt);
        BuildLiveSet(engines);
    }

    private void AdvanceSwap(double dt)
    {
        switch (_swap)
        {
            case SwapPhase.Out:
                _swapT += (float)(dt / Tuning.SwapOutSeconds);
                if (_swapT >= 1f)
                {
                    _swap = SwapPhase.In;
                    _swapT = 0f;
                }
                break;

            case SwapPhase.In:
                _swapT += (float)(dt / Tuning.SwapInSeconds);
                if (_swapT >= 1f)
                {
                    _swap = SwapPhase.Idle;
                    _swapT = 1f;
                }
                break;
        }
    }

    private void BuildLiveSet(List<EngineSample> engines)
    {
        _live.Clear();

        for (int i = 0; i < engines.Count; i++)
        {
            EngineSample engine = engines[i];
            ref readonly DotState dot = ref _dots[i];

            _live.Offsets.Add(new float2(engine.DiagramX, engine.DiagramY));
            _live.SizeFactors.Add(SizeFactorOf(engine));
            _live.Colors.Add(CurrentColor(in dot));
            _live.Burning.Add(dot.Shown is EngineStatus.Nominal or EngineStatus.Throttled);
        }
    }

    private void DrawEngines(
        ImDrawListPtr drawList,
        float2 center,
        float radius,
        float opacity,
        float scale,
        float introPhase)
    {
        bool retracting = _swap == SwapPhase.Out;
        DotSet set = retracting ? _outgoing : _live;

        if (set.Count == 0)
        {
            float labelSize = OverlayFonts.LabelSize * scale;
            float2 extent = Gfx.MeasureWithFont(OverlayFonts.Label, labelSize, "NO ENGINES".AsSpan());
            Gfx.TextCenteredFont(
                drawList, OverlayFonts.Label, labelSize,
                center.X, center.Y - extent.Y * 0.5f,
                OverlayStyle.TextDim, "NO ENGINES".AsSpan(), opacity);
            return;
        }

        float swapPhase = _swap switch
        {
            SwapPhase.Out => 1f - EaseOut(_swapT),
            SwapPhase.In => EaseOut(_swapT),
            _ => 1f,
        };

        float phase = MathF.Min(introPhase, swapPhase);

        if (opacity <= 0f || phase <= 0f)
        {
            return;
        }

        float margin = Tuning.EnvelopeMargin * scale;
        float plotRadius = MathF.Max(
            (radius - margin) / (1f + set.Normalised * set.MaxSizeFactor), 1f);
        plotRadius *= Tuning.ClusterFillFraction;
        float baseDotRadius = MathF.Max(set.Normalised * plotRadius, Tuning.MinDotRadius * scale);

        for (int i = 0; i < set.Count; i++)
        {
            float local = Math.Clamp(
                (phase - set.RingStarts[i]) / (1f - Tuning.RingStaggerSpan), 0f, 1f);

            if (local <= 0f)
            {
                continue;
            }

            float alpha = opacity * local;
            float popScale = Tuning.IntroDotStartScale + (1f - Tuning.IntroDotStartScale) * local;

            float2 offset = set.Offsets[i];

            float2 pos = new(
                center.X + offset.X * plotRadius,
                center.Y - offset.Y * plotRadius);

            uint color = set.Colors[i];

            float dotRadius =
                MathF.Max(baseDotRadius * set.SizeFactors[i], 2.5f * scale) * popScale;

            if (set.Burning[i])
            {
                // subtle glow around burning engines. prob should make this configurable but I'll do it later.
                drawList.AddCircleFilled(in pos, dotRadius * 1.1f,
                    OverlayStyle.WithOpacity(color, alpha * 0.20f));
            }

            drawList.AddCircleFilled(in pos, dotRadius, OverlayStyle.WithOpacity(color, alpha));
        }
    }

    private static float EaseOut(float t)
    {
        float inv = 1f - Math.Clamp(t, 0f, 1f);
        return 1f - inv * inv * inv;
    }

    private static float ComputeNormalisedDotRadius(List<EngineSample> engines)
    {
        int count = engines.Count;

        if (count == 1)
        {
            return Tuning.SingleEngineRadius;
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
            return MathF.Min(Tuning.SingleEngineRadius, 1f / MathF.Sqrt(count));
        }

        return MathF.Min(minSeparation * 0.5f * Tuning.NeighbourFillFraction, Tuning.SingleEngineRadius);
    }

    private static float[] ComputeRingStarts(List<EngineSample> engines, float dotRadius)
    {
        int count = engines.Count;
        float[] starts = new float[count];

        if (count < 2)
        {
            return starts;
        }

        float[] radii = new float[count];
        int[] order = new int[count];

        for (int i = 0; i < count; i++)
        {
            float x = engines[i].DiagramX;
            float y = engines[i].DiagramY;
            radii[i] = MathF.Sqrt(x * x + y * y);
            order[i] = i;
        }

        Array.Sort(radii, order);

        float tolerance = MathF.Max(dotRadius * Tuning.RingMergeTolerance, 0.01f);
        int[] ring = new int[count];
        int rings = 1;
        float bandBase = radii[0];

        for (int i = 1; i < count; i++)
        {
            if (radii[i] - bandBase > tolerance)
            {
                rings++;
                bandBase = radii[i];
            }

            ring[order[i]] = rings - 1;
        }

        if (rings < 2)
        {
            return starts;
        }

        for (int i = 0; i < count; i++)
        {
            starts[i] = Tuning.RingStaggerSpan * ring[i] / (float)(rings - 1);
        }

        return starts;
    }

    private static float SizeFactorOf(EngineSample engine)
        => engine.SizeFactor > 0f ? engine.SizeFactor : 1f;
}
