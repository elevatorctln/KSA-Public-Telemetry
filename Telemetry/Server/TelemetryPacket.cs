namespace KSATelemetryOverlay.Telemetry.Server;

public sealed class TelemetryPacket
{
    // Flight & Vehicle state
    public bool HasVehicle { get; set; }
    public bool HasLiftoff { get; set; }
    public bool HasSurfaceContact { get; set; }
    public bool OnRails { get; set; }
    public string Signal { get; set; } = "Live";
    public bool IsFrozen { get; set; }
    public bool CountingDown { get; set; }
    public double MissionTime { get; set; }
    public string VehicleName { get; set; } = string.Empty;
    public string MissionName { get; set; } = string.Empty;

    // Kinematics & Atmosphere
    public double SurfaceSpeed { get; set; }
    public double OrbitalSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public string SpeedRef { get; set; } = "Surface";
    public double Altitude { get; set; }
    public double RadarAltitude { get; set; }
    public bool TerrainRelative { get; set; }
    public double Apoapsis { get; set; }
    public double Periapsis { get; set; }
    public double GForce { get; set; }
    public float DynamicPressure { get; set; }
    public float MaxDynamicPressure { get; set; }
    public double Downrange { get; set; }

    // Propulsion & Engines
    public float Thrust { get; set; }
    public float ThrustToWeight { get; set; }
    public float PropellantFraction { get; set; }
    public float PropellantMass { get; set; }
    public float PropellantCapacity { get; set; }
    public int BurningEngines { get; set; }
    public int TotalEngines { get; set; }
    public EnginePacket[] Engines { get; set; } = [];

    // Events & Timeline
    public EventPacket[] Events { get; set; } = [];

    // User Configuration & Slot Customization
    public string[] LeftSlots { get; set; } = [];
    public string[] RightSlots { get; set; } = [];
    public bool ShowTelemetryBar { get; set; } = true;
    public bool ShowEngineDiagram { get; set; } = true;
    public bool ShowTimeline { get; set; } = true;
    public bool ShowMissionClock { get; set; } = true;
    public bool ShowPropellants { get; set; } = true;
    public float Scale { get; set; } = 0.83f;
    public float Opacity { get; set; } = 1f;
    public double SmoothingSeconds { get; set; } = 0.12;
    public double TimelineWindowSeconds { get; set; } = 240.0;
    public float SpeedArcFullScale { get; set; } = 7800f;
    public float AltitudeArcFullScaleKm { get; set; } = 100f;
    public float GForceArcFullScale { get; set; } = 6f;
    public float EngineDiagramRotation { get; set; } = 0f;
}

public sealed class EnginePacket
{
    public string Status { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Radius { get; set; }
    public float Throttle { get; set; }
    public bool Burning { get; set; }
}

public sealed class EventPacket
{
    public string Kind { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Explainer { get; set; } = string.Empty;
    public double Time { get; set; }
    public bool IsPrediction { get; set; }
}
