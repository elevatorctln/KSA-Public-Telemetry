namespace KSATelemetryOverlay.Telemetry;

public sealed class MissionEventLog
{
    private const double MinBurnBeforeCutoff = 1.0;
    private const double CutoffDebounce = 2.0;
    private const double StageSepDebounce = 2.0;
    private const float MinMeaningfulQ = 1_000f;
    private const float MaxQConfirmFraction = 0.95f;

    private readonly List<MissionEvent> _recorded = new(16);
    private readonly List<MissionEvent> _predicted = new(8);
    private readonly List<MissionEvent> _firedThisFrame = new(4);

    private string _baselineVehicle = string.Empty;

    private int _previousBurning;
    private int _previousPartCount;

    private bool _liftoffFired;
    private bool _maxQFired;
    private int _stageSepCount;

    private double _burningSince;
    private double _lastCutoffTime = double.NegativeInfinity;
    private double _lastStageSepTime = double.NegativeInfinity;

    private float _peakQ;
    private double _peakQTime;

    public IReadOnlyList<MissionEvent> Recorded => _recorded;
    public IReadOnlyList<MissionEvent> Predicted => _predicted;
    public IReadOnlyList<MissionEvent> FiredThisFrame => _firedThisFrame;

    public int Generation { get; private set; }

    public void Reset()
    {
        _recorded.Clear();
        _predicted.Clear();
        _firedThisFrame.Clear();

        _baselineVehicle = string.Empty;
        _previousBurning = 0;
        _previousPartCount = 0;

        _liftoffFired = false;
        _maxQFired = false;
        _stageSepCount = 0;

        _burningSince = 0.0;
        _lastCutoffTime = double.NegativeInfinity;
        _lastStageSepTime = double.NegativeInfinity;

        _peakQ = 0f;
        _peakQTime = 0.0;

        Generation++;
    }

    public void Update(TelemetrySnapshot snapshot, bool liftoffThisFrame, double dt)
    {
        _firedThisFrame.Clear();

        int burning = snapshot.BurningEngineCount;
        int parts = snapshot.PartCount;
        double now = snapshot.MissionElapsedSeconds;
        bool flying = snapshot.HasLiftoff;

        bool sameVehicle = string.Equals(_baselineVehicle, snapshot.VehicleName, StringComparison.Ordinal);

        if (!sameVehicle)
        {
            bool firstSight = _baselineVehicle.Length == 0;

            if (!firstSight && flying && parts < _previousPartCount)
            {
                DetectStaging(parts, flying, now);
            }

            _baselineVehicle = snapshot.VehicleName;
            _previousBurning = burning;
            _previousPartCount = parts;
            _burningSince = burning > 0 ? MinBurnBeforeCutoff : 0.0;
            return;
        }

        double endingRunLength = _burningSince;
        _burningSince = burning > 0 ? _burningSince + dt : 0.0;

        if (liftoffThisFrame && !_liftoffFired)
        {
            _liftoffFired = true;
            Record(new MissionEvent(MissionEventKind.Liftoff, 0.0));
        }

        DetectMaxQ(snapshot, flying, now);
        DetectStaging(parts, flying, now);
        DetectCutoff(burning, flying, now, endingRunLength);

        _previousBurning = burning;
        _previousPartCount = parts;
    }

    private void DetectMaxQ(TelemetrySnapshot snapshot, bool flying, double now)
    {
        if (!flying || _maxQFired)
        {
            return;
        }

        float q = snapshot.DynamicPressure;

        if (q > _peakQ)
        {
            _peakQ = q;
            _peakQTime = now;
            return;
        }

        if (_peakQ >= MinMeaningfulQ && q < _peakQ * MaxQConfirmFraction)
        {
            _maxQFired = true;
            Record(new MissionEvent(MissionEventKind.MaxQ, _peakQTime));
        }
    }

    private void DetectStaging(int parts, bool flying, double now)
    {
        if (!flying || parts >= _previousPartCount)
        {
            return;
        }

        if (now - _lastStageSepTime < StageSepDebounce)
        {
            return;
        }

        _lastStageSepTime = now;
        _stageSepCount++;
        Record(new MissionEvent(MissionEventKind.StageSep, now));
    }

    private void DetectCutoff(int burning, bool flying, double now, double endingRunLength)
    {
        if (!flying || burning > 0 || _previousBurning == 0)
        {
            return;
        }

        if (endingRunLength < MinBurnBeforeCutoff || now - _lastCutoffTime < CutoffDebounce)
        {
            return;
        }

        _lastCutoffTime = now;

        MissionEventKind kind = _stageSepCount switch
        {
            0 => MissionEventKind.Meco,
            1 => MissionEventKind.Seco,
            _ => MissionEventKind.EngineCutoff,
        };

        Record(new MissionEvent(kind, now));
    }

    private void Record(MissionEvent missionEvent)
    {
        _recorded.Add(missionEvent);
        _firedThisFrame.Add(missionEvent);
    }
    
    public void SetPredictions(ReadOnlySpan<double> missionTimes)
    {
        _predicted.Clear();

        for (int i = 0; i < missionTimes.Length; i++)
        {
            _predicted.Add(new MissionEvent(
                MissionEventKind.PlannedBurn, missionTimes[i], isPrediction: true));
        }
    }

    public void ClearPredictions() => _predicted.Clear();
}
