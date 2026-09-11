namespace KSATelemetryOverlay.Telemetry;
public enum EngineStatus : byte
{
    Inactive,

    Armed,

    Throttled,

    Nominal,

    Starved,
}

public struct EngineSample
{
    public EngineStatus Status;
    public float DiagramX;
    public float DiagramY;
    public float Throttle;
    public float ChamberPressure;
    public float ChamberTemperature;
    public float MassFlowRate;
    public double ThrustTimeRemaining;
    public int Sequence;
    public float ExitRadius;
    public float SizeFactor;

    public readonly bool IsBurning => Status is EngineStatus.Throttled or EngineStatus.Nominal;
}

public sealed class TelemetrySnapshot
{
    public bool HasVehicle;
    public bool OnRails;
    public bool HasSurfaceContact;
    public bool IsControllable;
    public double MissionElapsedSeconds;
    public bool HasLiftoff;
    public SignalStatus Signal;
    public bool IsFrozen;
    public string VehicleName = string.Empty;
    public double SurfaceSpeed;
    public double OrbitalSpeed;
    public double VerticalSpeed;
    public double Altitude;
    public double Apoapsis;
    public double Periapsis;
    public double GLoad;
    public float TotalMass;
    public float PropellantMass;
    public float Thrust;
    public float ThrustToWeight;
    public float AmbientPressure;
    public float AmbientDensity;
    public float DynamicPressure;
    public float MaxDynamicPressure;
    public readonly List<EngineSample> Engines = new(16);
    public float PropellantFraction;
    public float PropellantCapacity;
    public int BurningEngineCount;
    public int TotalEngineCount;
    public int PartCount;
    public bool HasLaunched;
    public double LaunchUniverseSeconds;
    public bool ClockEpochInferred;
    public MissionEventLog? Events;
    public void Clear()
    {
        HasVehicle = false;
        OnRails = false;
        HasSurfaceContact = false;
        IsControllable = false;
        MissionElapsedSeconds = 0;
        HasLiftoff = false;
        Signal = SignalStatus.NoVehicle;
        IsFrozen = false;
        VehicleName = string.Empty;
        SurfaceSpeed = OrbitalSpeed = VerticalSpeed = Altitude = Apoapsis = Periapsis = GLoad = 0;
        TotalMass = PropellantMass = Thrust = ThrustToWeight = 0f;
        PropellantFraction = PropellantCapacity = 0f;
        AmbientPressure = AmbientDensity = DynamicPressure = 0f;
        Engines.Clear();
        BurningEngineCount = TotalEngineCount = 0;
        PartCount = 0;
        HasLaunched = false;
        LaunchUniverseSeconds = 0;
        ClockEpochInferred = false;
        Events = null;
    }

    public void ResetRecords() => MaxDynamicPressure = 0f;
}
