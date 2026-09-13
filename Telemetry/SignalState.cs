namespace KSATelemetryOverlay.Telemetry;

public enum SignalStatus : byte
{
    NoVehicle,
    Live,
    Lost,
}

public sealed class SignalState
{
    private string _vehicleId = string.Empty;
    public SignalStatus Status { get; private set; } = SignalStatus.NoVehicle;
    public double SecondsSinceLoss { get; private set; }
    public bool LostThisFrame { get; private set; }
    public bool RegainedThisFrame { get; private set; }
    public bool IsFrozen => Status == SignalStatus.Lost;

    public void Reset()
    {
        _vehicleId = string.Empty;
        Status = SignalStatus.NoVehicle;
        SecondsSinceLoss = 0.0;
        LostThisFrame = false;
        RegainedThisFrame = false;
    }

    public void SetNoVehicle()
    {
        LostThisFrame = false;
        RegainedThisFrame = false;

        if (Status != SignalStatus.NoVehicle)
        {
            Status = SignalStatus.NoVehicle;
            SecondsSinceLoss = 0.0;
            _vehicleId = string.Empty;
        }
    }
    public void MarkLost(string vehicleId, double dt)
    {
        LostThisFrame = false;
        RegainedThisFrame = false;

        _vehicleId = vehicleId;

        if (Status == SignalStatus.Lost)
        {
            SecondsSinceLoss += dt;
            return;
        }

        Status = SignalStatus.Lost;
        SecondsSinceLoss = 0.0;
        LostThisFrame = true;
    }
    public bool ShouldSample(string vehicleId, bool isControllable, bool failureImminent, double dt)
    {
        LostThisFrame = false;
        RegainedThisFrame = false;

        if (!string.Equals(_vehicleId, vehicleId, StringComparison.Ordinal))
        {
            _vehicleId = vehicleId;
            Status = SignalStatus.NoVehicle;
            SecondsSinceLoss = 0.0;
        }

        if (isControllable && !failureImminent)
        {
            RegainedThisFrame = Status == SignalStatus.Lost;
            Status = SignalStatus.Live;
            SecondsSinceLoss = 0.0;
            return true;
        }

        if (Status == SignalStatus.Live)
        {
            Status = SignalStatus.Lost;
            SecondsSinceLoss = 0.0;
            LostThisFrame = true;
            return false;
        }

        if (Status == SignalStatus.Lost)
        {
            SecondsSinceLoss += dt;
            return false;
        }
        
        Status = SignalStatus.Live;
        return true;
    }
}
