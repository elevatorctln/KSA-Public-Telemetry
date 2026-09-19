namespace KSATelemetryOverlay.Telemetry;

public enum MissionEventKind : byte
{
    Liftoff,
    MaxQ,
    Meco,
    StageSep,
    BoosterSep,
    SecondStageIgnition,
    Ignition,
    LandingBurn,
    Seco,
    EngineCutoff,
    Landing,
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
        MissionEventKind.BoosterSep   => "BOOSTER SEP".AsSpan(),
        MissionEventKind.SecondStageIgnition    => "SES-1".AsSpan(),
        MissionEventKind.Ignition     => "ENGINE IGNITION".AsSpan(),
        MissionEventKind.LandingBurn  => "LANDING BURN".AsSpan(),
        MissionEventKind.Seco         => "SECO".AsSpan(),
        MissionEventKind.EngineCutoff => "ENGINE CUTOFF".AsSpan(),
        MissionEventKind.Landing      => "LANDING".AsSpan(),
        MissionEventKind.PlannedBurn  => "BURN".AsSpan(),
        _                             => "EVENT".AsSpan(),
    };

    public ReadOnlySpan<char> Explainer => Kind switch
    {
        MissionEventKind.Liftoff      => "Vehicle has left the pad.".AsSpan(),
        MissionEventKind.MaxQ         => "Point of maximum aerodynamic stress on the vehicle.".AsSpan(),
        MissionEventKind.Meco         => "Main engine cutoff.".AsSpan(),
        MissionEventKind.StageSep     => "Stage separation.".AsSpan(),
        MissionEventKind.BoosterSep   => "Boosters separated.".AsSpan(),
        MissionEventKind.SecondStageIgnition    => "Second stage engine start.".AsSpan(),
        MissionEventKind.Ignition     => "Engine relight.".AsSpan(),
        MissionEventKind.LandingBurn  => "Final burn before touchdown.".AsSpan(),
        MissionEventKind.Seco         => "Second stage engine cutoff.".AsSpan(),
        MissionEventKind.EngineCutoff => "Engines have shut down.".AsSpan(),
        MissionEventKind.Landing      => "Vehicle has landed.".AsSpan(),
        MissionEventKind.PlannedBurn  => "Planned maneuver.".AsSpan(),
        _                             => default,
    };

    public bool DeservesCallout => !IsPrediction;
}
