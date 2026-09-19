using Brutal.ImGuiApi;
using KSA;
using KSATelemetryOverlay.Config;
using KSATelemetryOverlay.Rendering;
using KSATelemetryOverlay.Telemetry;
using StarMap.API;

namespace KSATelemetryOverlay;

[StarMapMod]
public class TelemetryOverlayMod
{
    private const string LogPrefix = "[KSATelemetryOverlay] ";

    private readonly TelemetrySnapshot _snapshot = new();
    private readonly WindowHost _windows = new();

    private OverlayConfig? _config;
    private OverlayRenderer? _renderer;

    private bool _disabled;
    private int _consecutiveFailures;
    private string? _modDirectory;
    private string _lastWindowVehicle = string.Empty;

    [StarMapBeforeMain]
    public void OnBeforeMain()
    {
        Console.WriteLine(LogPrefix + "loading");
    }

    [StarMapImmediateLoad]
    public void OnImmediateLoad(Mod mod)
    {
        _modDirectory = mod.DirectoryPath;
        Console.WriteLine(LogPrefix + $"attached to KSA mod '{mod.Id}'");
    }

    [StarMapAllModsLoaded]
    public void OnAllModsLoaded()
    {
        try
        {
            _config = ConfigStore.Load();
            TelemetrySampler.Missions.ApplyEpochs(_config.MissionEpochs, _config.MissionOrigins);
            HiddenUiPatch.Install(DrawWhileGameUiHidden);

            Console.WriteLine(LogPrefix + (HiddenUiPatch.Installed
                ? "overlay will stay up when the game UI is hidden."
                : $"overlay will hide with the game UI: {HiddenUiPatch.Failure}."));
            _renderer = new OverlayRenderer(_config);
            SettingsUi.Bind(_config, _windows);
            TuningUi.Bind(_renderer);

            Console.WriteLine(
                LogPrefix + $"ready - {_config.ToggleKey} toggles the overlay, " +
                $"{_config.SettingsKey} opens settings (or use the Mods menu).");
        }
        catch (Exception ex)
        {
            Fail("starting up", ex);
        }
    }

    [StarMapAfterOnFrame]
    public void OnAfterFrame(double currentPlayerTime, double dtPlayer)
    {
        if (_disabled || _config is null)
        {
            return;
        }

        try
        {
            TelemetrySampler.Sample(_snapshot, _config, dtPlayer);
            HiddenUiPatch.RecordDelta(dtPlayer);
            Succeeded();
        }
        catch (Exception ex)
        {
            Fail("sampling", ex);
        }
    }

    [StarMapAfterGui]
    public void OnAfterGui(double dt) => DrawEverything(dt, gameUiHidden: false);

    private void DrawWhileGameUiHidden(double dt)
    {
        if (_config is null || !_config.ShowWhenGameUiHidden)
        {
            return;
        }

        DrawEverything(dt, gameUiHidden: true);
    }

    private void DrawEverything(double dt, bool gameUiHidden)
    {
        if (_disabled || _config is null || _renderer is null)
        {
            return;
        }

        try
        {
            OverlayFonts.TryLoad(_modDirectory);

            _windows.EnsureBuilt(_config);
            _windows.Update(_snapshot, _config, dt);

            if (!string.Equals(_lastWindowVehicle, _snapshot.VehicleName, StringComparison.Ordinal))
            {
                _lastWindowVehicle = _snapshot.VehicleName;
                _windows.ResetPanels();
            }

            HandleHotkeys(_config);

            _renderer.Draw(_snapshot, dt);

            if (!gameUiHidden)
            {
                SettingsUi.DrawFallbackWindow();
                TuningUi.Draw();
            }

            if (_windows.CaptureInto(_config))
            {
                ConfigStore.MarkDirty();
            }

            if (TelemetrySampler.Missions.EpochsChanged)
            {
                _config.MissionEpochs = TelemetrySampler.Missions.CaptureEpochs();
                _config.MissionOrigins = TelemetrySampler.Missions.CaptureOrigins();
                ConfigStore.MarkDirty();
            }

            ConfigStore.Flush(dt);
            Succeeded();
        }
        catch (Exception ex)
        {
            Fail("drawing", ex);
        }
    }

    [StarMapUnload]
    public void OnUnload()
    {
        if (_config is not null)
        {
            _windows.CaptureInto(_config);
            ConfigStore.SaveNow();
        }

        _windows.HideAll();

        try
        {
            _renderer?.RestoreFlightUi();
        }
        catch (Exception ex)
        {
            Console.WriteLine(LogPrefix + $"could not restore the flight HUD: {ex.Message}");
        }

        TelemetrySampler.Reset();
        Console.WriteLine(LogPrefix + "unloaded");
    }

    private static void HandleHotkeys(OverlayConfig config)
    {
        if (ImGui.GetIO().WantTextInput || SettingsUi.IsCapturingKey)
        {
            return;
        }

        if (ImGui.IsKeyPressed(config.ToggleKey, repeat: false))
        {
            config.Enabled = !config.Enabled;
            ConfigStore.MarkDirty();
        }

        if (ImGui.IsKeyPressed(config.SettingsKey, repeat: false))
        {
            SettingsUi.ToggleWindow();
        }

        if (ImGui.IsKeyPressed(config.CountdownKey, repeat: false))
        {
            if (TelemetrySampler.IsCountingDown)
            {
                TelemetrySampler.CancelCountdown();
            }
            else
            {
                TelemetrySampler.StartCountdown(config.CountdownSeconds);
            }
        }
    }

    private const int FailureTolerance = 10;
    private void Succeeded()
    {
        _consecutiveFailures = 0;
    }

    private void Fail(string stage, Exception ex)
    {
        _consecutiveFailures++;

        if (_consecutiveFailures == 1)
        {
            Console.WriteLine(LogPrefix + $"error while {stage}: {ex}");
        }

        if (_consecutiveFailures < FailureTolerance)
        {
            return;
        }

        _disabled = true;
        Console.WriteLine(
            LogPrefix + $"disabled after {FailureTolerance} consecutive errors while {stage}.");

        try
        {
            _renderer?.RestoreFlightUi();
        }
        catch (Exception restoreEx)
        {
            Console.WriteLine(LogPrefix + $"could not restore the flight HUD: {restoreEx.Message}");
        }
    }
}
