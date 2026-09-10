using KSA;
using KSATelemetryOverlay.Telemetry;
using StarMap.API;

namespace KSATelemetryOverlay;

[StarMapMod]
public class TelemetryOverlayMod
{
    private const string LogPrefix = "[TelemetryOverlay] ";

    private readonly TelemetrySnapshot _snapshot = new();
    private readonly OverlayConfig _config = new();
    private readonly Rendering.OverlayRenderer _renderer;

    private bool _disabled;

    public TelemetryOverlayMod()
    {
        _renderer = new Rendering.OverlayRenderer(_config);
    }

    [StarMapBeforeMain]
    public void OnBeforeMain()
    {
        Console.WriteLine(LogPrefix + "loading");
    }

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        Console.WriteLine(LogPrefix + $"attached to KSA mod '{mod.Id}'");
    }

    [StarMapAllModsLoaded]
    public void OnAllModsLoaded()
    {
        Console.WriteLine(LogPrefix + $"ready - press {_config.ToggleKey} to toggle the overlay");
    }

    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            TelemetrySampler.Sample(_snapshot);
        }
        catch (Exception ex)
        {
            Fail("sampling", ex);
        }
    }

    [StarMapAfterGui]
    public void OnAfterGui(double dt)
    {
        if (_disabled)
        {
            return;
        }

        try
        {
            _renderer.Draw(_snapshot, dt);
        }
        catch (Exception ex)
        {
            Fail("drawing", ex);
        }
    }

    [StarMapUnload]
    public void OnUnload()
    {
        Console.WriteLine(LogPrefix + "unloaded");
    }

    private void Fail(string stage, Exception ex)
    {
        _disabled = true;
        Console.WriteLine(LogPrefix + $"disabled after an error while {stage}: {ex}");
    }
}
