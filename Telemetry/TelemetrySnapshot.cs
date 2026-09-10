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

public struct PropellantSample
{
    public string Name;
    public float Mass;
    public float Fraction;
    public float ColorR, ColorG, ColorB;
    public bool HasColor;
}

public sealed class TelemetrySnapshot
{
    public bool HasVehicle;
    public bool OnRails;

    public string VehicleName = string.Empty;
    public double SurfaceSpeed;
    public double OrbitalSpeed;
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
    public readonly List<PropellantSample> Propellants = new(8);

    public int BurningEngineCount;
    public int TotalEngineCount;
    public void Clear()
    {
        HasVehicle = false;
        OnRails = false;
        VehicleName = string.Empty;
        SurfaceSpeed = OrbitalSpeed = Altitude = Apoapsis = Periapsis = GLoad = 0;
        TotalMass = PropellantMass = Thrust = ThrustToWeight = 0f;
        AmbientPressure = AmbientDensity = DynamicPressure = 0f;
        Engines.Clear();
        Propellants.Clear();
        BurningEngineCount = TotalEngineCount = 0;
    }

    public void ResetRecords() => MaxDynamicPressure = 0f;
}
