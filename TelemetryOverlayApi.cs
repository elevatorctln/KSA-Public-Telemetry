using KSATelemetryOverlay.Config;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay;

/// <summary>
/// Public API for optional companion mods (e.g. BrowserServerModule / OBS integration)
/// to hook into telemetry sampling and contribute settings without hard dependencies.
/// </summary>
public static class TelemetryOverlayApi
{
    /// <summary>
    /// Current configuration instance, or null before mods are loaded.
    /// </summary>
    public static OverlayConfig? CurrentConfig { get; internal set; }

    /// <summary>
    /// Fired every frame after telemetry is sampled from the game.
    /// Passes the live TelemetrySnapshot, active OverlayConfig, and player frame delta dt.
    /// </summary>
    public static event Action<TelemetrySnapshot, OverlayConfig, double>? OnTelemetrySampled;

    /// <summary>
    /// Fired when rendering settings to allow companion modules to draw their own UI sections.
    /// Returns true if any settings values were modified (so config is marked dirty).
    /// </summary>
    public static event Func<OverlayConfig, bool>? OnDrawSettings;

    /// <summary>
    /// True if an OBS / Browser companion module has registered a settings handler.
    /// </summary>
    public static bool HasObsModule => OnDrawSettings is not null;

    internal static void NotifyTelemetrySampled(TelemetrySnapshot snapshot, OverlayConfig config, double dt)
    {
        if (OnTelemetrySampled is null)
        {
            return;
        }

        Delegate[] list = OnTelemetrySampled.GetInvocationList();
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] is Action<TelemetrySnapshot, OverlayConfig, double> handler)
            {
                try
                {
                    handler(snapshot, config, dt);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[KSATelemetryOverlay] Error in companion module OnTelemetrySampled: {ex.Message}");
                }
            }
        }
    }

    internal static bool DrawObsSettings(OverlayConfig config)
    {
        if (OnDrawSettings is null)
        {
            return false;
        }

        bool changed = false;
        Delegate[] list = OnDrawSettings.GetInvocationList();
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] is Func<OverlayConfig, bool> handler)
            {
                try
                {
                    changed |= handler(config);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[KSATelemetryOverlay] Error in companion module OnDrawSettings: {ex.Message}");
                }
            }
        }
        return changed;
    }
}
