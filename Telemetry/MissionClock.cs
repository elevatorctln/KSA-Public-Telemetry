namespace KSATelemetryOverlay.Telemetry;
public sealed class MissionClock
{
    private const double MinAscentRate = 0.5;

    private double _liftoffUniverseSeconds;
    private bool _hasLiftoff;
    private bool _sawGrounded;
    private double _countdownZero;
    private bool _hasCountdown;
    public double ElapsedSeconds { get; private set; }
    public double LiftoffUniverseSeconds => _liftoffUniverseSeconds;
    public bool HasLiftoff => _hasLiftoff;
    public bool LiftoffThisFrame { get; private set; }

    public bool EpochInferred { get; private set; }
    public bool HasCountdown => _hasCountdown && !_hasLiftoff;
    public double CountdownZero => _countdownZero;

    public void SetCountdown(double zeroUniverseSeconds)
    {
        if (_hasLiftoff)
        {
            return;
        }

        _countdownZero = zeroUniverseSeconds;
        _hasCountdown = true;
    }

    public void CancelCountdown()
    {
        _hasCountdown = false;
        _countdownZero = 0.0;
    }

    public void Reset()
    {
        _liftoffUniverseSeconds = 0.0;
        _hasLiftoff = false;
        _sawGrounded = false;
        ElapsedSeconds = 0.0;
        LiftoffThisFrame = false;
        EpochInferred = false;
        CancelCountdown();
    }
    public void SeedLiftoff(double liftoffUniverseSeconds)
    {
        _liftoffUniverseSeconds = liftoffUniverseSeconds;
        _hasLiftoff = true;
        _sawGrounded = true;
        EpochInferred = false;
        LiftoffThisFrame = false;
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

        if (hasSurfaceContact)
        {
            _sawGrounded = true;
        }

        if (!_hasLiftoff)
        {
            bool observedLiftoff = _sawGrounded
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
            else if (!_sawGrounded && hasLaunched)
            {
                _liftoffUniverseSeconds = launchGameSeconds;
                _hasLiftoff = true;
                EpochInferred = true;
            }
            else
            {
                ElapsedSeconds = _hasCountdown ? nowUniverseSeconds - _countdownZero : 0.0;
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
    {
        double epoch = _hasLiftoff
            ? _liftoffUniverseSeconds
            : _hasCountdown ? _countdownZero : nowUniverseSeconds;

        return universeSeconds - epoch;
    }
}
