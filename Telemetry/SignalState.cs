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
    public bool IsFrozen => Status == SignalStatus.Lost;

    public void Reset()
    {
        _vehicleId = string.Empty;
        Status = SignalStatus.NoVehicle;
        SecondsSinceLoss = 0.0;
        LostThisFrame = false;
    }

    public void SetNoVehicle()
    {
        LostThisFrame = false;

        if (Status != SignalStatus.NoVehicle)
        {
            Status = SignalStatus.NoVehicle;
            SecondsSinceLoss = 0.0;
            _vehicleId = string.Empty;
        }
    }

    public bool ShouldSample(string vehicleId, bool isControllable, double dt)
    {
        LostThisFrame = false;

        if (!string.Equals(_vehicleId, vehicleId, StringComparison.Ordinal))
        {
            _vehicleId = vehicleId;
            Status = SignalStatus.NoVehicle;
            SecondsSinceLoss = 0.0;
        }

        if (isControllable)
        {
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
