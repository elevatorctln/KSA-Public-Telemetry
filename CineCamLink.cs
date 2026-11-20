using System.Reflection;

namespace KSATelemetryOverlay;

/// Optional link to CineCam (a camera mod with takes, replays and renders), found by reflection so the overlay
/// loads fine without it. CineCam's events are documented in docs/integration.md of the CineCam repo.
/// With "Follow CineCam cues" on, the overlay only shows between the cue "overlay-in" of a CineCam take and
/// the cue "overlay-out", the end of a live take, or the close of a replay.
public static class CineCamLink
{
    private const string CueInName = "overlay-in";
    private const string CueOutName = "overlay-out";

    private static readonly List<(EventInfo Event, Delegate Handler)> _attached = [];
    private static Type? _api;
    private static bool _replayOpen;

    public static bool Available => _api is not null;

    /// True between "overlay-in" and "overlay-out" (or the end of the take).
    public static bool CueIn { get; private set; }

    /// True when the overlay last went out because a replay jumped (play from another time, a render start,
    /// the close), so it should cut away instead of playing the outro into the first frames of the new run.
    public static bool CutAway { get; private set; }

    public static bool TryAttach()
    {
        Detach();

        Type? api = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType("CineCam.Api.CineCamEvents", throwOnError: false))
            .FirstOrDefault(type => type is not null);

        if (api is null || api.GetField("ApiVersion")?.GetValue(null) is not int version || version != 1)
        {
            return false;
        }

        _api = api;
        _replayOpen = api.GetProperty("IsReplayOpen")?.GetValue(null) is true;

        bool ok = Attach("TakeStarted", nameof(OnTakeStarted))
            & Attach("TakeEnded", nameof(OnTakeEnded))
            & Attach("Cue", nameof(OnCue))
            & Attach("ReplayOpened", nameof(OnReplayOpened))
            & Attach("ReplayClosed", nameof(OnReplayClosed));

        if (!ok)
        {
            Detach();
        }

        return ok;
    }

    public static void Detach()
    {
        foreach ((EventInfo info, Delegate handler) in _attached)
        {
            info.RemoveEventHandler(null, handler);
        }

        _attached.Clear();
        _api = null;
        _replayOpen = false;
        CueIn = false;
        CutAway = false;
    }

    private static bool Attach(string eventName, string handlerName)
    {
        EventInfo? info = _api!.GetEvent(eventName, BindingFlags.Public | BindingFlags.Static);
        MethodInfo? method = typeof(CineCamLink).GetMethod(handlerName, BindingFlags.NonPublic | BindingFlags.Static);

        if (info?.EventHandlerType is null || method is null)
        {
            return false;
        }

        // A handler whose parameters no longer match gives no delegate, so an API change fails here
        Delegate? handler = Delegate.CreateDelegate(info.EventHandlerType, method, throwOnBindFailure: false);

        if (handler is null)
        {
            return false;
        }

        info.AddEventHandler(null, handler);
        _attached.Add((info, handler));
        return true;
    }

    // A replay starts a new run at each play, seek and render, possibly after a cue, so the state comes
    // from the cues that already fired.
    private static void OnTakeStarted(string take, string mode, bool interfaceHidden)
    {
        string[] passed = _api?.GetProperty("PassedCues")?.GetValue(null) as string[] ?? [];
        int lastIn = Array.FindLastIndex(passed, cue => cue.Equals(CueInName, StringComparison.OrdinalIgnoreCase));
        int lastOut = Array.FindLastIndex(passed, cue => cue.Equals(CueOutName, StringComparison.OrdinalIgnoreCase));
        CueIn = lastIn > lastOut;
        CutAway = true;
    }

    // In an open replay the take only paused or moved, so the overlay keeps its state until the next run.
    private static void OnTakeEnded(string take, string mode, string reason)
    {
        if (!_replayOpen)
        {
            CueIn = false;
            CutAway = false;
        }
    }

    private static void OnCue(string name, double takeSeconds, double gameSeconds)
    {
        if (name.Equals(CueInName, StringComparison.OrdinalIgnoreCase))
        {
            CueIn = true;
        }
        else if (name.Equals(CueOutName, StringComparison.OrdinalIgnoreCase))
        {
            CueIn = false;
            CutAway = false;
        }
    }

    private static void OnReplayOpened(string recording)
    {
        _replayOpen = true;
        CueIn = false;
        CutAway = true;
    }

    private static void OnReplayClosed(string recording)
    {
        _replayOpen = false;
        CueIn = false;
        CutAway = true;
    }
}
