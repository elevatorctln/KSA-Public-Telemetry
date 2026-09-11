namespace KSATelemetryOverlay.Telemetry;
public sealed class Mission(Int128 key)
{
    public readonly Int128 Key = key;
    public readonly MissionClock Clock = new();
    public readonly MissionEventLog Events = new();
    public double LastSeenUniverseSeconds;
    public long LastTouched;

    public void Reset()
    {
        Clock.Reset();
        Events.Reset();
    }
}

public sealed class MissionRegistry
{
    private const double RevertToleranceSeconds = 1.0;
    private const int Capacity = 8;
    private const int EpochCapacity = 32;
    private readonly Dictionary<Int128, Mission> _missions = [];
    private readonly Dictionary<Int128, double> _epochs = [];
    private long _clock;
    public int Count => _missions.Count;

    public bool EpochsChanged { get; private set; }

    public Mission Resolve(Int128 launchKey, double nowUniverseSeconds)
    {
        if (!_missions.TryGetValue(launchKey, out Mission? mission))
        {
            mission = new Mission(launchKey);
            _missions[launchKey] = mission;
            Evict();
        }
        else if (nowUniverseSeconds < mission.LastSeenUniverseSeconds - RevertToleranceSeconds)
        {
            mission.Reset();
        }

        if (!mission.Clock.HasLiftoff
            && _epochs.TryGetValue(launchKey, out double epoch)
            && epoch <= nowUniverseSeconds)
        {
            mission.Clock.SeedLiftoff(epoch);
        }

        mission.LastSeenUniverseSeconds = nowUniverseSeconds;
        mission.LastTouched = ++_clock;

        return mission;
    }

    public void RecordLiftoff(Int128 launchKey, double liftoffUniverseSeconds)
    {
        if (_epochs.TryGetValue(launchKey, out double known)
            && Math.Abs(known - liftoffUniverseSeconds) < 1e-6)
        {
            return;
        }

        _epochs[launchKey] = liftoffUniverseSeconds;
        EpochsChanged = true;
        EvictEpochs();
    }

    public Dictionary<string, double> CaptureEpochs()
    {
        EpochsChanged = false;

        Dictionary<string, double> saved = new(_epochs.Count, StringComparer.Ordinal);

        foreach (KeyValuePair<Int128, double> entry in _epochs)
        {
            saved[entry.Key.ToString()] = entry.Value;
        }

        return saved;
    }

    public void ApplyEpochs(Dictionary<string, double>? saved)
    {
        _epochs.Clear();
        EpochsChanged = false;

        if (saved is null)
        {
            return;
        }

        foreach (KeyValuePair<string, double> entry in saved)
        {
            if (Int128.TryParse(entry.Key, out Int128 key))
            {
                _epochs[key] = entry.Value;
            }
        }

        EvictEpochs();
    }

    public void Clear() => _missions.Clear();
    private void EvictEpochs()
    {
        while (_epochs.Count > EpochCapacity)
        {
            Int128 oldest = default;
            bool found = false;

            foreach (Int128 key in _epochs.Keys)
            {
                if (!found || key < oldest)
                {
                    oldest = key;
                    found = true;
                }
            }

            _epochs.Remove(oldest);
        }
    }

    private void Evict()
    {
        while (_missions.Count > Capacity)
        {
            Int128 oldestKey = default;
            long oldest = long.MaxValue;

            foreach (KeyValuePair<Int128, Mission> entry in _missions)
            {
                if (entry.Value.LastTouched < oldest)
                {
                    oldest = entry.Value.LastTouched;
                    oldestKey = entry.Key;
                }
            }

            _missions.Remove(oldestKey);
        }
    }
}
