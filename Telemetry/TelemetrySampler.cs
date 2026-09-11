using System.Runtime.InteropServices;
using Brutal.Numerics;
using KSA;

namespace KSATelemetryOverlay.Telemetry;

public static class TelemetrySampler
{
    private const double StandardGravity = 9.80665;
    private const float NominalThrottleThreshold = 0.95f;
    private static string _lastVehicleId = string.Empty;
    private static readonly SignalState _signal = new();
    private static readonly MissionRegistry _missions = new();
    private static readonly List<double> _burnTimes = new(8);
    public static SignalState Signal => _signal;
    public static MissionRegistry Missions => _missions;
    public static void Reset()
    {
        _lastVehicleId = string.Empty;
        _signal.Reset();
        _missions.Clear();
        _overlapGroup.Clear();
        _burnTimes.Clear();
    }

    public static void Sample(TelemetrySnapshot snapshot, double dt)
    {
        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle is null || vehicle.IsDisposed)
        {
            snapshot.Clear();
            _lastVehicleId = string.Empty;
            _signal.SetNoVehicle();
            return;
        }

        if (!string.Equals(_lastVehicleId, vehicle.Id, StringComparison.Ordinal))
        {
            _lastVehicleId = vehicle.Id;
            snapshot.ResetRecords();
        }

        if (!_signal.ShouldSample(vehicle.Id, vehicle.IsControllable, dt))
        {
            snapshot.Signal = _signal.Status;
            snapshot.IsFrozen = true;
            return;
        }

        snapshot.Clear();

        snapshot.HasVehicle = true;
        snapshot.VehicleName = vehicle.Id;
        snapshot.OnRails = vehicle.Situation.IsOnRails();
        snapshot.HasSurfaceContact = vehicle.Situation.HasAnyContact();
        snapshot.IsControllable = vehicle.IsControllable;
        snapshot.Signal = _signal.Status;
        snapshot.IsFrozen = false;

        SampleKinematics(vehicle, snapshot);
        SampleEngines(vehicle, snapshot);
        SamplePropellants(vehicle, snapshot);

        double now = Universe.GetElapsedSeconds();
        UniverseTime launchTime = vehicle.LaunchGameTime;

        snapshot.HasLaunched = vehicle.HasLaunched;
        snapshot.LaunchUniverseSeconds = launchTime.Seconds();

        Mission mission = _missions.Resolve(launchTime.Nanoseconds, now);

        mission.Clock.Update(
            now,
            snapshot.LaunchUniverseSeconds,
            snapshot.HasLaunched,
            snapshot.HasSurfaceContact,
            snapshot.VerticalSpeed,
            snapshot.BurningEngineCount > 0);

        if (mission.Clock.LiftoffThisFrame)
        {
            _missions.RecordLiftoff(mission.Key, mission.Clock.LiftoffUniverseSeconds);
        }

        snapshot.MissionElapsedSeconds = mission.Clock.ElapsedSeconds;
        snapshot.HasLiftoff = mission.Clock.HasLiftoff;
        snapshot.ClockEpochInferred = mission.Clock.EpochInferred;

        mission.Events.Update(
            snapshot, mission.Clock.LiftoffThisFrame, dt, CountMissionVehicles(launchTime.Nanoseconds));
        SamplePlannedBurns(vehicle, mission, now);
        snapshot.Events = mission.Events;
    }
    
    private static int CountMissionVehicles(Int128 launchKey)
    {
        CelestialSystem? system = Universe.CurrentSystem;

        if (system is null)
        {
            return 0;
        }

        LookupCollection<Astronomical> all = system.All;
        int count = 0;

        for (int i = 0; i < all.Count; i++)
        {
            if (all.GetIndex(i) is Vehicle other
                && !other.IsDisposed
                && other.LaunchGameTime.Nanoseconds == launchKey)
            {
                count++;
            }
        }

        return count;
    }

    private static void SamplePlannedBurns(Vehicle vehicle, Mission mission, double nowUniverseSeconds)
    {
        _burnTimes.Clear();

        FlightComputer? computer = vehicle.FlightComputer;
        BurnPlan? plan = computer?.BurnPlan;

        if (plan is null || !plan.HasActiveBurns)
        {
            mission.Events.ClearPredictions();
            return;
        }

        int count = plan.BurnCount;
        for (int i = 0; i < count; i++)
        {
            if (!plan.TryGetBurn(i, out Burn? burn) || burn is null || !burn.HasDeltaV)
            {
                continue;
            }

            _burnTimes.Add(mission.Clock.MissionTimeFor(burn.Time.Seconds(), nowUniverseSeconds));
        }

        mission.Events.SetPredictions(CollectionsMarshal.AsSpan(_burnTimes));
    }

    private static void SampleKinematics(Vehicle vehicle, TelemetrySnapshot snapshot)
    {
        snapshot.SurfaceSpeed = vehicle.GetSurfaceSpeed();
        snapshot.OrbitalSpeed = vehicle.GetInertialSpeed();
        snapshot.Altitude = vehicle.GetRadarAltitude();
        snapshot.Apoapsis = vehicle.Apoapsis;
        snapshot.Periapsis = vehicle.Periapsis;
        snapshot.GLoad = vehicle.AccelerationBody.Length() / StandardGravity;

        snapshot.TotalMass = vehicle.TotalMass;
        snapshot.PropellantMass = vehicle.PropellantMass;

        ReadOnlyPhysicsStates physics = vehicle.GetPhysicsStates();

        physics.GetStatesCci(out double3 positionCci, out double3 velocityCci, out _);
        double radius = positionCci.Length();
        snapshot.VerticalSpeed = radius > 0.0
            ? double3.Dot(velocityCci, positionCci / radius)
            : 0.0;

        float ambientPressure = physics.Environment.AtmosphericPressure;
        float ambientDensity = physics.Environment.AtmosphericDensity;
        float airspeed = physics.ComputeAirVelocityBody().Length();
        double gravity = physics.Environment.GravitationBub.Length();

        snapshot.AmbientPressure = ambientPressure;
        snapshot.AmbientDensity = ambientDensity;
        snapshot.DynamicPressure = 0.5f * ambientDensity * airspeed * airspeed;

        if (snapshot.DynamicPressure > snapshot.MaxDynamicPressure)
        {
            snapshot.MaxDynamicPressure = snapshot.DynamicPressure;
        }

        snapshot.Thrust = vehicle.ComputeActiveThrust(ambientPressure);

        double weight = snapshot.TotalMass * gravity;
        snapshot.ThrustToWeight = weight > 0.0 ? (float)(snapshot.Thrust / weight) : 0f;
    }
    private static void SampleEngines(Vehicle vehicle, TelemetrySnapshot snapshot)
    {
        PartTree parts = vehicle.Parts;
        if (parts is null)
        {
            return;
        }

        Span<EngineController> controllers = parts.Modules.Get<EngineController>();

        int activeSequence = parts.SequenceList?.ActiveSequence ?? 0;
        snapshot.PartCount = parts.Count;

        for (int i = 0; i < controllers.Length; i++)
        {
            EngineController controller = controllers[i];
            bool controllerActive = controller.IsActive;

            if (controller.Sequence != 0 && controller.Sequence != activeSequence && !controllerActive)
            {
                continue;
            }

            float largestExit = 0f;
            for (int c = 0; c < controller.Cores.Length; c++)
            {
                largestExit = MathF.Max(largestExit, GetMaxExitRadius(controller.Cores[c]));
            }
            float mainEngineThreshold = largestExit * MainNozzleRadiusFraction;

            var coreEnumerator = parts.RocketCores
                .GetModulesAndStates(controller.Cores.AsSpan())
                .GetEnumerator();

            while (coreEnumerator.MoveNext())
            {
                var core = coreEnumerator.Current;
                ref readonly RocketCoreState coreState = ref core.State;

                float exitRadius = GetMaxExitRadius(core.Module);
                if (largestExit > 0f && exitRadius < mainEngineThreshold)
                {
                    continue;
                }

                EngineSample sample = default;
                sample.ExitRadius = exitRadius;
                sample.Sequence = controller.Sequence;
                sample.Throttle = coreState.Throttle;
                sample.ChamberPressure = coreState.Conditions.Core.Pressure;
                sample.ChamberTemperature = coreState.Conditions.Core.Temperature;
                sample.MassFlowRate = coreState.MassFlowRate;
                sample.ThrustTimeRemaining = coreState.GetActualThrustTime();
                sample.Status = ClassifyEngine(controllerActive, in coreState);

                GetNozzleCentroid(core.Module, out float x, out float y);
                sample.DiagramX = x;
                sample.DiagramY = y;

                if (sample.IsBurning)
                {
                    snapshot.BurningEngineCount++;
                }

                snapshot.Engines.Add(sample);
            }
        }

        snapshot.TotalEngineCount = snapshot.Engines.Count;
        ComputeSizeFactors(snapshot);
        NormaliseDiagram(snapshot);
    }

    private static EngineStatus ClassifyEngine(bool controllerActive, ref readonly RocketCoreState state)
    {
        if (!controllerActive)
        {
            return EngineStatus.Inactive;
        }

        if (!state.IsPropellantAvailable)
        {
            return EngineStatus.Starved;
        }

        if (state.Throttle <= 0f)
        {
            return EngineStatus.Armed;
        }

        return state.Throttle >= NominalThrottleThreshold
            ? EngineStatus.Nominal
            : EngineStatus.Throttled;
    }

    private static void ComputeSizeFactors(TelemetrySnapshot snapshot)
    {
        int count = snapshot.Engines.Count;
        if (count == 0)
        {
            return;
        }

        float maxRadius = 0f;
        for (int i = 0; i < count; i++)
        {
            maxRadius = MathF.Max(maxRadius, snapshot.Engines[i].ExitRadius);
        }

        for (int i = 0; i < count; i++)
        {
            EngineSample e = snapshot.Engines[i];
            e.SizeFactor = maxRadius > 0f ? Math.Clamp(e.ExitRadius / maxRadius, 0.25f, 1f) : 1f;
            snapshot.Engines[i] = e;
        }
    }

    private const float MainNozzleRadiusFraction = 0.4f;
    private static float GetMaxExitRadius(RocketCore core)
    {
        RocketNozzle[]? nozzles = core.Rocket?.Nozzles;
        if (nozzles is null)
        {
            return 0f;
        }

        float max = 0f;
        for (int i = 0; i < nozzles.Length; i++)
        {
            max = MathF.Max(max, nozzles[i].FxExitRadius);
        }
        return max;
    }

    private static void GetNozzleCentroid(RocketCore core, out float x, out float y)
    {
        x = 0f;
        y = 0f;

        RocketNozzle[]? nozzles = core.Rocket?.Nozzles;
        if (nozzles is null || nozzles.Length == 0)
        {
            return;
        }

        float sumY = 0f, sumZ = 0f;
        int count = 0;

        for (int i = 0; i < nozzles.Length; i++)
        {
            RocketNozzle nozzle = nozzles[i];
            Part? part = nozzle.Parent;
            if (part is null)
            {
                continue;
            }

            float4x4 asmb2Vehicle = float4x4.Pack(part.MatrixAsmb2VehicleAsmb);
            float3 vehicleLocation = nozzle.LocationAsmb.Transform(asmb2Vehicle);

            sumY += vehicleLocation.Y;
            sumZ += vehicleLocation.Z;
            count++;
        }

        if (count == 0)
        {
            return;
        }

        x = sumY / count;
        y = sumZ / count;
    }

    private static void NormaliseDiagram(TelemetrySnapshot snapshot)
    {
        int count = snapshot.Engines.Count;
        if (count == 0)
        {
            return;
        }

        if (count == 1)
        {
            EngineSample only = snapshot.Engines[0];
            only.DiagramX = 0f;
            only.DiagramY = 0f;
            snapshot.Engines[0] = only;
            return;
        }
        float centreX = 0f, centreY = 0f;
        for (int i = 0; i < count; i++)
        {
            centreX += snapshot.Engines[i].DiagramX;
            centreY += snapshot.Engines[i].DiagramY;
        }
        centreX /= count;
        centreY /= count;

        float halfSpan = 0f;
        for (int i = 0; i < count; i++)
        {
            EngineSample e = snapshot.Engines[i];
            float dx = e.DiagramX - centreX;
            float dy = e.DiagramY - centreY;
            halfSpan = MathF.Max(halfSpan, MathF.Sqrt(dx * dx + dy * dy));
        }

        if (halfSpan <= 1e-4f)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = MathF.Tau * i / count;
                EngineSample engine = snapshot.Engines[i];
                engine.DiagramX = MathF.Cos(angle) * 0.7f;
                engine.DiagramY = MathF.Sin(angle) * 0.7f;
                snapshot.Engines[i] = engine;
            }
            return;
        }

        float scale = 1f / halfSpan;
        for (int i = 0; i < count; i++)
        {
            EngineSample engine = snapshot.Engines[i];
            engine.DiagramX = (engine.DiagramX - centreX) * scale;
            engine.DiagramY = (engine.DiagramY - centreY) * scale;
            snapshot.Engines[i] = engine;
        }

        SeparateOverlaps(snapshot);

        float maxRadius = 0f;
        for (int i = 0; i < count; i++)
        {
            EngineSample e = snapshot.Engines[i];
            maxRadius = MathF.Max(maxRadius, MathF.Sqrt(e.DiagramX * e.DiagramX + e.DiagramY * e.DiagramY));
        }

        if (maxRadius > 1f)
        {
            float shrink = 1f / maxRadius;
            for (int i = 0; i < count; i++)
            {
                EngineSample e = snapshot.Engines[i];
                e.DiagramX *= shrink;
                e.DiagramY *= shrink;
                snapshot.Engines[i] = e;
            }
        }
    }
    private const float OverlapEpsilon = 0.04f;
    private static void SeparateOverlaps(TelemetrySnapshot snapshot)
    {
        int count = snapshot.Engines.Count;
        Span<bool> handled = count <= 64 ? stackalloc bool[count] : new bool[count];

        for (int i = 0; i < count; i++)
        {
            if (handled[i])
            {
                continue;
            }

            EngineSample a = snapshot.Engines[i];

            _overlapGroup.Clear();
            _overlapGroup.Add(i);

            for (int j = i + 1; j < count; j++)
            {
                if (handled[j])
                {
                    continue;
                }

                EngineSample b = snapshot.Engines[j];
                float dx = b.DiagramX - a.DiagramX;
                float dy = b.DiagramY - a.DiagramY;
                if (dx * dx + dy * dy <= OverlapEpsilon * OverlapEpsilon)
                {
                    _overlapGroup.Add(j);
                }
            }

            foreach (int idx in _overlapGroup)
            {
                handled[idx] = true;
            }

            int groupSize = _overlapGroup.Count;
            if (groupSize < 2)
            {
                continue;
            }

            float radius = MathF.Min(0.18f + 0.02f * groupSize, 0.35f);
            for (int k = 0; k < groupSize; k++)
            {
                float angle = MathF.Tau * k / groupSize;
                int idx = _overlapGroup[k];
                EngineSample engine = snapshot.Engines[idx];
                engine.DiagramX = a.DiagramX + MathF.Cos(angle) * radius;
                engine.DiagramY = a.DiagramY + MathF.Sin(angle) * radius;
                snapshot.Engines[idx] = engine;
            }
        }
    }

    private static readonly List<int> _overlapGroup = new(16);

    /// Sums propellant mass and capacity across every tank on the vehicle, no need to differentiate.
    private static void SamplePropellants(Vehicle vehicle, TelemetrySnapshot snapshot)
    {
        PartTree parts = vehicle.Parts;
        if (parts?.Tanks is null || parts.Moles is null)
        {
            return;
        }

        ReadOnlySpan<MoleState> moleStates = parts.Moles.States;
        Span<Tank> tanks = parts.Tanks.Modules;

        float aggregateMass = 0f;
        float aggregateCapacity = 0f;

        for (int t = 0; t < tanks.Length; t++)
        {
            List<Mole> moles = tanks[t].Moles;

            for (int m = 0; m < moles.Count; m++)
            {
                Mole mole = moles[m];
                int idx = mole.StatesIdx;
                if (idx < 0 || idx >= moleStates.Length)
                {
                    continue;
                }

                float capacity = mole.GetStoredMass(mole.ContainerVolume);

                if (capacity <= 0f)
                {
                    continue;
                }

                aggregateMass += moleStates[idx].Mass;
                aggregateCapacity += capacity;
            }
        }

        snapshot.PropellantCapacity = aggregateCapacity;
        snapshot.PropellantFraction = aggregateCapacity > 0f
            ? Math.Clamp(aggregateMass / aggregateCapacity, 0f, 1f)
            : 0f;
    }

}
