namespace KSATelemetryOverlay.Rendering;

public readonly struct IntroPhases(float backdrop, float gauges, float arcSweep, float readouts)
{
    public readonly float Backdrop = backdrop;
    public readonly float Gauges = gauges;
    public readonly float ArcSweep = arcSweep;
    public readonly float Readouts = readouts;
    public static IntroPhases Complete => new(1f, 1f, 1f, 1f);

    public bool IsComplete =>
        Backdrop >= 1f && Gauges >= 1f && ArcSweep >= 1f && Readouts >= 1f;
}

public sealed class IntroAnimator
{
    private readonly record struct Stage(double Start, double Duration, int Decay);

    private static readonly Stage Backdrop = new(0.00, 0.42, 3);
    private static readonly Stage Gauges   = new(0.12, 0.60, 3);
    private static readonly Stage ArcSweep = new(0.12, 1.5, 15);
    private static readonly Stage Readouts = new(0.46, 0.56, 3);

    private static readonly double TotalSeconds = new[]
    {
        Backdrop.Start + Backdrop.Duration,
        Gauges.Start + Gauges.Duration,
        ArcSweep.Start + ArcSweep.Duration,
        Readouts.Start + Readouts.Duration,
    }.Max();

    private double _elapsed = TotalSeconds;
    public bool IsPlaying => _elapsed < TotalSeconds;
    public void Restart() => _elapsed = 0.0;
    public void Finish() => _elapsed = TotalSeconds;

    public void Update(double dt)
    {
        if (_elapsed < TotalSeconds)
        {
            _elapsed += Math.Clamp(dt, 0.0, 0.1);
        }
    }

    public IntroPhases Phases => IsPlaying
        ? new IntroPhases(
            Progress(Backdrop), Progress(Gauges), Progress(ArcSweep), Progress(Readouts))
        : IntroPhases.Complete;

    private float Progress(Stage stage)
    {
        if (stage.Duration <= 0.0)
        {
            return 1f;
        }

        double t = (_elapsed - stage.Start) / stage.Duration;

        if (t <= 0.0)
        {
            return 0f;
        }

        if (t >= 1.0)
        {
            return 1f;
        }

        return (float)(1.0 - Falloff(1.0 - t, stage.Decay));
    }

    private static double Falloff(double inverse, int decay)
    {
        double result = inverse;

        for (int i = 1; i < decay; i++)
        {
            result *= inverse;
        }

        return result;
    }
}
