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
    private readonly record struct Stage(double Start, double Duration, int Decay)
    {
        public double VisibleEnd =>
            Start + Duration * (1.0 - Math.Pow(0.01, 1.0 / Math.Max(Decay, 1)));
    }
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
    private static double VisibleSeconds => Math.Max(
        Math.Max(Backdrop.VisibleEnd, Gauges.VisibleEnd),
        Math.Max(Rim.VisibleEnd, Math.Max(ArcSweep.VisibleEnd, Readouts.VisibleEnd)))
        * TimeScale
        + Math.Max(Tuning.GaugeStaggerMax, 0f);
    private double _elapsed = double.PositiveInfinity;
    private bool _reversing;
    private double _outroFrom;
    private double _outroTotal;
    private double _outroLeft;

    public bool IsPlaying => _reversing ? _elapsed > 0.0 : _elapsed < TotalSeconds;
    public bool IsHidden => _reversing && _elapsed <= 0.0;

    public bool IsReversing => _reversing;

    public void Restart()
    {
        _elapsed = _reversing ? Math.Clamp(_elapsed, 0.0, TotalSeconds) : 0.0;
        _reversing = false;
    }

    public void Finish()
    {
        _elapsed = double.PositiveInfinity;
        _reversing = false;
    }

    public void Reverse()
    {
        if (_reversing)
        {
            return;
        }

        _outroFrom = double.IsFinite(_elapsed)
            ? Math.Min(_elapsed, VisibleSeconds)
            : VisibleSeconds;

        _outroTotal = Math.Max(Tuning.OutroSeconds, 0.05f);
        _outroLeft = _outroTotal;
        _elapsed = _outroFrom;
        _reversing = true;
    }

    public void Update(double dt)
    {
        double step = Math.Clamp(dt, 0.0, 0.1);

        if (_reversing)
        {
            _outroLeft = Math.Max(0.0, _outroLeft - step);
            _elapsed = _outroFrom * Math.Clamp(_outroLeft / _outroTotal, 0.0, 1.0);
            return;
        }

        if (_elapsed < TotalSeconds)
        {
            _elapsed += step;
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
