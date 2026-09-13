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
    private int _previousVehicleCount = -1;
    private int _previousRadialDecouplers = -1;

    private bool _liftoffFired;
    private bool _maxQFired;
    private int _cutoffCount;
    private double _burnStartedAt = double.NegativeInfinity;
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
        _previousVehicleCount = -1;
        _previousRadialDecouplers = -1;

        _liftoffFired = false;
        _maxQFired = false;
        _cutoffCount = 0;

        _burnStartedAt = double.NegativeInfinity;
        _lastCutoffTime = double.NegativeInfinity;
        _lastStageSepTime = double.NegativeInfinity;

        _peakQ = 0f;
        _peakQTime = 0.0;

        Generation++;
    }

    public void Update(
        TelemetrySnapshot snapshot, bool liftoffThisFrame, int missionVehicleCount, bool rebaseline)
    {
        _firedThisFrame.Clear();

        int burning = snapshot.BurningEngineCount;
        int parts = snapshot.PartCount;
        double now = snapshot.MissionElapsedSeconds;
        bool flying = snapshot.HasLiftoff;

        bool gainedVehicle = _previousVehicleCount >= 0 && missionVehicleCount > _previousVehicleCount;
        _previousVehicleCount = missionVehicleCount;

        int radial = snapshot.AttachedRadialDecouplers;
        bool releasedRadial = _previousRadialDecouplers >= 0 && radial < _previousRadialDecouplers;
        _previousRadialDecouplers = radial;

        DetectStaging(gainedVehicle, releasedRadial, flying, now);

        bool sameVehicle = string.Equals(_baselineVehicle, snapshot.VehicleName, StringComparison.Ordinal);

        if (!sameVehicle || rebaseline)
        {
            _baselineVehicle = snapshot.VehicleName;
            _previousBurning = burning;
            _previousPartCount = parts;
            _burnStartedAt = burning > 0 ? now - MinBurnBeforeCutoff : double.NegativeInfinity;
            return;
        }

        if (burning > 0 && _previousBurning == 0)
        {
            _burnStartedAt = now;
        }

        double endingRunLength = burning > 0 || _burnStartedAt == double.NegativeInfinity
            ? 0.0
            : now - _burnStartedAt;

        if (liftoffThisFrame && !_liftoffFired)
        {
            _liftoffFired = true;
            Record(new MissionEvent(MissionEventKind.Liftoff, 0.0));
        }

        DetectMaxQ(snapshot, flying, now);
        DetectStaging(parts < _previousPartCount, releasedRadial, flying, now);
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

    private void DetectStaging(bool separated, bool radial, bool flying, double now)
    {
        if (!flying || !separated)
        {
            return;
        }

        if (now - _lastStageSepTime < StageSepDebounce)
        {
            return;
        }

        _lastStageSepTime = now;

        Record(new MissionEvent(
            radial ? MissionEventKind.BoosterSep : MissionEventKind.StageSep, now));
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

        MissionEventKind kind = _cutoffCount switch
        {
            0 => MissionEventKind.Meco,
            1 => MissionEventKind.Seco,
            _ => MissionEventKind.EngineCutoff,
        };

        _cutoffCount++;
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
