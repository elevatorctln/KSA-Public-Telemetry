namespace KSATelemetryOverlay.Rendering;

public readonly struct IntroPhases(
    float backdrop, float gauges, float rim, float arcSweep, float readouts)
{
    public readonly float Backdrop = backdrop;
    public readonly float Gauges = gauges;
    public readonly float Rim = rim;
    public readonly float ArcSweep = arcSweep;
    public readonly float Readouts = readouts;
    public static IntroPhases Complete => new(1f, 1f, 1f, 1f, 1f);

    public bool IsComplete =>
        Backdrop >= 1f && Gauges >= 1f && Rim >= 1f && ArcSweep >= 1f && Readouts >= 1f;

}

public sealed class IntroAnimator
{
    private readonly record struct Stage(double Start, double Duration, int Decay);
    private static Stage Backdrop => new(Tuning.BackdropStart, Tuning.BackdropDuration, Tuning.BackdropDecay);
    private static Stage Gauges   => new(Tuning.GaugesStart,   Tuning.GaugesDuration,   Tuning.GaugesDecay);
    private static Stage Rim      => new(Tuning.RimStart,      Tuning.RimDuration,      Tuning.RimDecay);
    private static Stage ArcSweep => new(Tuning.ArcSweepStart, Tuning.ArcSweepDuration, Tuning.ArcSweepDecay);
    private static Stage Readouts => new(Tuning.ReadoutsStart, Tuning.ReadoutsDuration, Tuning.ReadoutsDecay);

    private static double TimeScale => Math.Max(Tuning.IntroTimeScale, 0.01f);

    private static double SequenceSeconds => Math.Max(
        Math.Max(Backdrop.Start + Backdrop.Duration, Gauges.Start + Gauges.Duration),
        Math.Max(Rim.Start + Rim.Duration,
            Math.Max(ArcSweep.Start + ArcSweep.Duration, Readouts.Start + Readouts.Duration)))
        * TimeScale;

    private static double TotalSeconds => SequenceSeconds + Math.Max(Tuning.GaugeStaggerMax, 0f);
    private double _elapsed = double.PositiveInfinity;

    public bool IsPlaying => _elapsed < TotalSeconds;
    public void Restart() => _elapsed = 0.0;
    public void Finish() => _elapsed = double.PositiveInfinity;

    public void Update(double dt)
    {
        if (_elapsed < TotalSeconds)
        {
            _elapsed += Math.Clamp(dt, 0.0, 0.1);
        }
    }

    public IntroPhases Phases => PhasesDelayedBy(0.0);
    public IntroPhases PhasesDelayedBy(double delaySeconds)
    {
        double at = _elapsed - Math.Max(delaySeconds, 0.0);

        if (at >= SequenceSeconds)
        {
            return IntroPhases.Complete;
        }

        return new IntroPhases(
            Progress(Backdrop, at), Progress(Gauges, at), Progress(Rim, at),
            Progress(ArcSweep, at), Progress(Readouts, at));
    }

    private float Progress(Stage stage, double elapsed)
    {
        double duration = stage.Duration * TimeScale;

        if (duration <= 0.0)
        {
            return 1f;
        }

        double t = (elapsed - stage.Start * TimeScale) / duration;

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
