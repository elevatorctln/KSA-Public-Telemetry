namespace KSATelemetryOverlay.Telemetry;
public sealed class MissionClock
{
    private const double MinAscentRate = 0.5;

    private double _liftoffUniverseSeconds;
    private bool _hasLiftoff;
    private bool _sawGrounded;
    public double ElapsedSeconds { get; private set; }
    public double LiftoffUniverseSeconds => _liftoffUniverseSeconds;
    public bool HasLiftoff => _hasLiftoff;
    public bool LiftoffThisFrame { get; private set; }

    public bool EpochInferred { get; private set; }

    public void Reset()
    {
        _liftoffUniverseSeconds = 0.0;
        _hasLiftoff = false;
        _sawGrounded = false;
        ElapsedSeconds = 0.0;
        LiftoffThisFrame = false;
        EpochInferred = false;
    }

    /// <summary>
    /// Adopts a liftoff time worked out on an earlier run and stored since. Takes
    /// the clock straight to a running state, so the detection below never fires
    /// and the count picks up exactly where it left off.
    /// </summary>
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

        // Touching the ground is the only trustworthy "has not left yet" signal.
        // HasLaunched is not one: KSA sets it on the pre-placed craft at universe
        // load (Universe.AssignStartingCrew -> MarkLaunched), so those report
        // launched while sitting on the pad, and LaunchGameTime is the vehicle's
        // CONSTRUCTION time, not a liftoff. Between them they had the clock
        // counting from spawn.
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
                // Never seen on the ground, so we joined it already under way -
                // a save loaded mid-flight, or a switch to something in orbit.
                // Construction time is the only anchor there is; flag it as a guess.
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
