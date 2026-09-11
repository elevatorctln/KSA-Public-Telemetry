using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;
public sealed class WindowHost
{
    private static readonly (string Id, string Title, float2 Size)[] _catalogue = BuildCatalogue();

    private readonly List<TelemetryWindow> _windows = [];

    private TelemetrySnapshot? _snapshot;
    private OverlayConfig? _config;
    private double _dt;
    private bool _built;

    public IReadOnlyList<TelemetryWindow> Windows => _windows;

    public bool IsBuilt => _built;

    private static (string, string, float2)[] BuildCatalogue()
    {
        ReadoutKind[] kinds = Enum.GetValues<ReadoutKind>();
        List<(string, string, float2)> entries = new(kinds.Length + 3);

        foreach (ReadoutKind kind in kinds)
        {
            entries.Add((
                ReadoutWindowId(kind),
                new string(ReadoutPanel.LabelFor(kind)),
                new float2(170f, 170f)));
        }

        entries.Add(("engine_cluster", "Engine Cluster", new float2(200f, 200f)));
        entries.Add(("mission_clock", "Mission Clock", new float2(280f, 130f)));
        entries.Add(("timeline", "Timeline", new float2(600f, 110f)));

        return entries.ToArray();
    }

    private static string ReadoutWindowId(ReadoutKind kind) => "readout_" + kind;

    private static IOverlayPanel CreatePanel(string id) => id switch
    {
        "engine_cluster" => new EngineClusterPanel(),
        "mission_clock" => new MissionClockPanel(),
        "timeline" => new TimelinePanel(),
        _ => CreateReadout(id),
    };

    private static IOverlayPanel CreateReadout(string id)
    {
        const string prefix = "readout_";

        if (!id.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"No panel is registered for window id '{id}'.", nameof(id));
        }

        string name = id[prefix.Length..];
        ReadoutKind kind = Enum.TryParse(name, out ReadoutKind parsed) ? parsed : ReadoutKind.Speed;

        return new ReadoutPanel(id, kind);
    }

    public void EnsureBuilt(OverlayConfig config)
    {
        if (_built)
        {
            return;
        }

        _built = true;

        for (int i = 0; i < _catalogue.Length; i++)
        {
            (string id, string title, float2 size) = _catalogue[i];

            TelemetryWindow window = new(CreatePanel(id), title, size, this);

            if (FindState(config, id) is { } state)
            {
                window.SeedPlacement(new float2(state.X, state.Y), new float2(state.Width, state.Height));
                window.SetShown(state.Open);
            }

            _windows.Add(window);
        }
    }

    public void Update(TelemetrySnapshot snapshot, OverlayConfig config, double dt)
    {
        _snapshot = snapshot;
        _config = config;
        _dt = dt;
    }

    public bool TryGetFrame(
        out TelemetrySnapshot? snapshot, out OverlayConfig? config, out double dt)
    {
        snapshot = _snapshot;
        config = _config;
        dt = _dt;

        return snapshot is not null && config is not null;
    }

    public void HideAll()
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            _windows[i].SetShown(false);
        }
    }

    public void ResetPanels()
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            _windows[i].ResetPanel();
        }
    }

    public bool AnyOpen()
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            if (_windows[i].IsShown)
            {
                return true;
            }
        }

        return false;
    }

    public bool CaptureInto(OverlayConfig config)
    {
        const float epsilon = 0.5f;
        bool changed = false;

        for (int i = 0; i < _windows.Count; i++)
        {
            TelemetryWindow window = _windows[i];

            OverlayWindowState state = FindState(config, window.PanelId) ?? AddState(config, window.PanelId);

            if (state.Open != window.IsShown)
            {
                state.Open = window.IsShown;
                changed = true;
            }
            
            if (window.LastSize.X <= 0f || window.LastSize.Y <= 0f)
            {
                continue;
            }

            if (MathF.Abs(state.X - window.LastPosition.X) > epsilon
                || MathF.Abs(state.Y - window.LastPosition.Y) > epsilon
                || MathF.Abs(state.Width - window.LastSize.X) > epsilon
                || MathF.Abs(state.Height - window.LastSize.Y) > epsilon)
            {
                state.X = window.LastPosition.X;
                state.Y = window.LastPosition.Y;
                state.Width = window.LastSize.X;
                state.Height = window.LastSize.Y;
                changed = true;
            }
        }

        return changed;
    }

    private static OverlayWindowState? FindState(OverlayConfig config, string id)
    {
        for (int i = 0; i < config.Windows.Count; i++)
        {
            if (string.Equals(config.Windows[i].Id, id, StringComparison.Ordinal))
            {
                return config.Windows[i];
            }
        }

        return null;
    }

    private static OverlayWindowState AddState(OverlayConfig config, string id)
    {
        OverlayWindowState state = new() { Id = id };
        config.Windows.Add(state);
        return state;
    }
}
