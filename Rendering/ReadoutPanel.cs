using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;
public enum ReadoutKind : byte
{
    Speed,
    Altitude,
    GForce,
    VerticalSpeed,
    Thrust,
    ThrustToWeight,
    DynamicPressure,
    Apoapsis,
    Periapsis,
}

public sealed class ReadoutPanel : IOverlayPanel
{
    private const float CapsuleSize = 150f;
    private const float CapsuleGap = 3f;
    private const float IntroRingStartScale = 0.72f;
    private const float IntroTextStartScale = 0.82f;
    private readonly float[] _smoothed;
    private bool _initialised;

    public ReadoutPanel(string id, params ReadoutKind[] kinds)
    {
        Id = id;
        Kinds = kinds;
        _smoothed = new float[kinds.Length];
    }

    public string Id { get; }
    public string DisplayName => "Readouts";
    public ReadoutKind[] Kinds { get; }
    public bool IsVisible(in PanelContext context)
        => context.Config.ShowTelemetryBar && context.Snapshot.HasVehicle;

    public float2 Measure(in PanelContext context)
    {
        int n = Kinds.Length;
        if (n == 0)
        {
            return default;
        }

        return new float2(n * CapsuleSize + (n - 1) * CapsuleGap, CapsuleSize);
    }

    public void Reset() => _initialised = false;

    public void Draw(in PanelContext context, float2 origin, float2 size)
    {
        UpdateSmoothing(in context);

        float scale = context.Scale;
        float capsule = CapsuleSize * scale;
        float gap = CapsuleGap * scale;
        float radius = capsule * 0.42f;

        Span<char> buffer = stackalloc char[64];

        for (int i = 0; i < Kinds.Length; i++)
        {
            float2 center = new(
                origin.X + capsule * 0.5f + i * (capsule + gap),
                origin.Y + size.Y * 0.5f);

            IntroPhases intro = context.IntroFor(center.X, capsule);

            float ringRadius =
                radius * (IntroRingStartScale + (1f - IntroRingStartScale) * intro.Gauges);

            float ringOpacity = context.Opacity * intro.Gauges;
            float textOpacity = context.Opacity * intro.Readouts;

            ReadoutKind kind = Kinds[i];
            ReadOnlySpan<char> value = ValueFor(kind, _smoothed[i], buffer);

            if (ringOpacity > 0f)
            {
                Gfx.GaugePlate(context.DrawList, center, ringRadius, ringOpacity);

                float fullScale = FullScaleFor(kind);
                if (fullScale > 0f && intro.ArcSweep > 0f)
                {
                    DrawSweep(context.DrawList, center, ringRadius,
                        intro.ArcSweep, OverlayStyle.ArcTrack, ringOpacity, scale);

                    float level = Math.Clamp(_smoothed[i] / fullScale, 0f, 1f) * intro.ArcSweep;
                    DrawSweep(context.DrawList, center, ringRadius,
                        level, OverlayStyle.ArcFill, ringOpacity, scale);
                }

                Gfx.ReadoutBrackets(context.DrawList, center, ringRadius, ringOpacity, scale);
            }

            if (textOpacity > 0f)
            {
                Gfx.ReadoutText(
                    context.DrawList, center, radius,
                    LabelFor(kind), value, UnitFor(kind),
                    ColorFor(kind, _smoothed[i], context.Snapshot),
                    textOpacity, scale,
                    IntroTextStartScale + (1f - IntroTextStartScale) * intro.Readouts);
            }
        }
    }

    private static void DrawSweep(
        ImDrawListPtr drawList, float2 center, float radius,
        float level, uint color, float opacity, float scale)
    {
        if (level <= 0f)
        {
            return;
        }

        const float halfSweep = MathF.PI * 0.75f;
        float start = -halfSweep;
        float end = start + halfSweep * 2f * level;
        float sweepRadius = radius + 3f * scale;
        float thickness = MathF.Max(1.5f, 2.5f * scale);
        Gfx.Arc(drawList, center, sweepRadius, start, end, color, thickness, opacity);
    }

    private void UpdateSmoothing(in PanelContext context)
    {
        double tau = Math.Max(context.Config.SmoothingSeconds, 1e-4);
        double alpha = _initialised ? 1.0 - Math.Exp(-context.DeltaTime / tau) : 1.0;

        for (int i = 0; i < Kinds.Length; i++)
        {
            float target = RawValue(Kinds[i], context.Snapshot);
            _smoothed[i] += (float)((target - _smoothed[i]) * alpha);
        }

        _initialised = true;
    }

    private static float RawValue(ReadoutKind kind, TelemetrySnapshot s) => kind switch
    {
        ReadoutKind.Speed           => (float)s.SurfaceSpeed,
        ReadoutKind.Altitude        => (float)s.Altitude,
        ReadoutKind.GForce          => (float)s.GLoad,
        ReadoutKind.VerticalSpeed   => (float)s.VerticalSpeed,
        ReadoutKind.Thrust          => s.Thrust,
        ReadoutKind.ThrustToWeight  => s.ThrustToWeight,
        ReadoutKind.DynamicPressure => s.DynamicPressure,
        ReadoutKind.Apoapsis        => (float)s.Apoapsis,
        ReadoutKind.Periapsis       => (float)s.Periapsis,
        _                           => 0f,
    };

    public static ReadOnlySpan<char> LabelFor(ReadoutKind kind) => kind switch
    {
        ReadoutKind.Speed           => "SPEED".AsSpan(),
        ReadoutKind.Altitude        => "ALTITUDE".AsSpan(),
        ReadoutKind.GForce          => "G-FORCE".AsSpan(),
        ReadoutKind.VerticalSpeed   => "V/S".AsSpan(),
        ReadoutKind.Thrust          => "THRUST".AsSpan(),
        ReadoutKind.ThrustToWeight  => "T/W".AsSpan(),
        ReadoutKind.DynamicPressure => "DYN PRESS".AsSpan(),
        ReadoutKind.Apoapsis        => "APOAPSIS".AsSpan(),
        ReadoutKind.Periapsis       => "PERIAPSIS".AsSpan(),
        _                           => "--".AsSpan(),
    };

    private static ReadOnlySpan<char> ValueFor(
        ReadoutKind kind, float value, Span<char> buffer) => kind switch
    {
        ReadoutKind.Speed           => Format.Number(buffer, value * 3.6, "N0"),
        ReadoutKind.Altitude        => Format.Number(buffer, value / 1000.0, "N1"),
        ReadoutKind.GForce          => Format.Number(buffer, value, "N1"),
        ReadoutKind.VerticalSpeed   => Format.Number(buffer, value, "N0"),
        ReadoutKind.Thrust          => Format.Force(buffer, value),
        ReadoutKind.ThrustToWeight  => Format.Number(buffer, value, "N2"),
        ReadoutKind.DynamicPressure => Format.Pressure(buffer, value),
        ReadoutKind.Apoapsis        => Format.Distance(buffer, value),
        ReadoutKind.Periapsis       => Format.Distance(buffer, value),
        _                           => "--".AsSpan(),
    };

    public static ReadOnlySpan<char> UnitFor(ReadoutKind kind) => kind switch
    {
        ReadoutKind.Speed         => "KM/H".AsSpan(),
        ReadoutKind.Altitude      => "KM".AsSpan(),
        ReadoutKind.GForce        => "G".AsSpan(),
        ReadoutKind.VerticalSpeed => "M/S".AsSpan(),
        _                         => default,
    };

    private static float FullScaleFor(ReadoutKind kind) => kind switch
    {
        ReadoutKind.Speed  => 7800f,
        ReadoutKind.GForce => 6f,
        _ => 0f,
    };

    private static uint ColorFor(ReadoutKind kind, float value, TelemetrySnapshot s) => kind switch
    {
        ReadoutKind.GForce when value >= 4f => OverlayStyle.TextPrimary,
        ReadoutKind.DynamicPressure when s.MaxDynamicPressure > 0f
            && s.DynamicPressure < s.MaxDynamicPressure * 0.98f => OverlayStyle.TextDim,

        _ => OverlayStyle.TextPrimary,
    };
}
