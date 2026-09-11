namespace KSATelemetryOverlay.Telemetry;

public enum MissionEventKind : byte
{
    Liftoff,
    MaxQ,
    Meco,
    StageSep,
    Seco,
    EngineCutoff,
    PlannedBurn,
}

public readonly struct MissionEvent(
    MissionEventKind kind, double missionTime, bool isPrediction = false)
{
    public readonly MissionEventKind Kind = kind;
    public readonly double MissionTime = missionTime;
    public readonly bool IsPrediction = isPrediction;
    public ReadOnlySpan<char> Label => Kind switch
    {
        MissionEventKind.Liftoff      => "LIFTOFF".AsSpan(),
        MissionEventKind.MaxQ         => "MAX Q".AsSpan(),
        MissionEventKind.Meco         => "MECO".AsSpan(),
        MissionEventKind.StageSep     => "STAGE SEP".AsSpan(),
        MissionEventKind.Seco         => "SECO".AsSpan(),
        MissionEventKind.EngineCutoff => "ENGINE CUTOFF".AsSpan(),
        MissionEventKind.PlannedBurn  => "BURN".AsSpan(),
        _                             => "EVENT".AsSpan(),
    };

    public ReadOnlySpan<char> Explainer => Kind switch
    {
        MissionEventKind.Liftoff      => "(Vehicle has left the pad)".AsSpan(),
        MissionEventKind.MaxQ         => "(Maximum dynamic pressure)".AsSpan(),
        MissionEventKind.Meco         => "(Main engine cutoff)".AsSpan(),
        MissionEventKind.StageSep     => "(Stage separation)".AsSpan(),
        MissionEventKind.Seco         => "(Second stage engine cutoff)".AsSpan(),
        MissionEventKind.EngineCutoff => "(Engines shut down)".AsSpan(),
        MissionEventKind.PlannedBurn  => "(Planned maneuver)".AsSpan(),
        _                             => default,
    };

    public bool DeservesCallout => !IsPrediction;
}
