namespace KSATelemetryOverlay.Telemetry;
public sealed class MissionClock
{
    private const double MinAscentRate = 0.5;

    private double _liftoffUniverseSeconds;
    private bool _hasLiftoff;
    private bool _sawPreLaunch;
    public double ElapsedSeconds { get; private set; }
    public bool HasLiftoff => _hasLiftoff;
    public bool LiftoffThisFrame { get; private set; }

    public bool EpochInferred { get; private set; }

    public void Reset()
    {
        _liftoffUniverseSeconds = 0.0;
        _hasLiftoff = false;
        _sawPreLaunch = false;
        ElapsedSeconds = 0.0;
        LiftoffThisFrame = false;
        EpochInferred = false;
    }

    public void Update(
        double nowUniverseSeconds,
        double launchGameSeconds,
        bool hasLaunched,
        bool hasSurfaceContact,
        double verticalSpeed,
        bool isUnderPower)
    {
        LiftoffThisFrame = false;

        if (!hasLaunched)
        {
            _sawPreLaunch = true;
        }

        if (!_hasLiftoff)
        {
            bool observedLiftoff = _sawPreLaunch
                && !hasSurfaceContact
                && isUnderPower
                && verticalSpeed > MinAscentRate;

            if (observedLiftoff)
            {
                _liftoffUniverseSeconds = nowUniverseSeconds;
                _hasLiftoff = true;
                EpochInferred = false;
                LiftoffThisFrame = true;
            }
            else if (hasLaunched)
            {
                _liftoffUniverseSeconds = launchGameSeconds;
                _hasLiftoff = true;
                EpochInferred = true;
            }
            else
            {
                ElapsedSeconds = 0.0;
                return;
            }
        }

        if (nowUniverseSeconds < _liftoffUniverseSeconds)
        {
            _liftoffUniverseSeconds = nowUniverseSeconds;
        }

        ElapsedSeconds = nowUniverseSeconds - _liftoffUniverseSeconds;
    }

    public double MissionTimeFor(double universeSeconds, double nowUniverseSeconds)
        => universeSeconds - (_hasLiftoff ? _liftoffUniverseSeconds : nowUniverseSeconds);
}
