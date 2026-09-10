using KSA;

namespace KSATelemetryOverlay.Telemetry;

public sealed class MissionClock
{
    private const double MinAscentRate = 0.5;
    private string _vehicleId = string.Empty;
    private double _liftoffTime;
    private bool _hasLiftoff;
    public double ElapsedSeconds { get; private set; }
    public bool HasLiftoff => _hasLiftoff;
    public bool LiftoffThisFrame { get; private set; }

    public void Reset()
    {
        _vehicleId = string.Empty;
        _liftoffTime = 0.0;
        _hasLiftoff = false;
        ElapsedSeconds = 0.0;
        LiftoffThisFrame = false;
    }

    public void Update(string vehicleId, bool hasSurfaceContact, double verticalSpeed, bool isUnderPower)
    {
        LiftoffThisFrame = false;

        if (!string.Equals(_vehicleId, vehicleId, StringComparison.Ordinal))
        {
            _vehicleId = vehicleId;
            _liftoffTime = 0.0;
            _hasLiftoff = false;
            ElapsedSeconds = 0.0;
        }

        double now = Universe.GetElapsedSeconds();

        if (!_hasLiftoff)
        {
            if (!hasSurfaceContact && isUnderPower && verticalSpeed > MinAscentRate)
            {
                _liftoffTime = now;
                _hasLiftoff = true;
                LiftoffThisFrame = true;
            }
            else
            {
                ElapsedSeconds = 0.0;
                return;
            }
        }

        if (now < _liftoffTime)
        {
            _liftoffTime = now;
        }

        ElapsedSeconds = now - _liftoffTime;
    }
}
