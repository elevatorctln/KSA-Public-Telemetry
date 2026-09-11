using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSA;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class TelemetryWindow : ImGuiWindow, IStaticWindow
{
    private readonly IOverlayPanel _panel;
    private readonly WindowHost _host;

    public TelemetryWindow(IOverlayPanel panel, string title, float2 initialSize, WindowHost host)
        : base(initialSize, lockAspectRatio: false, show: false)
    {
        _panel = panel;
        _host = host;

        PanelId = panel.Id;
        DisplayTitle = title;
        SetWindowTitle(title);
    }

    public string PanelId { get; }
    public string DisplayTitle { get; }
    public float2 LastPosition { get; private set; }

    public float2 LastSize { get; private set; }
    public void SeedPlacement(float2 position, float2 size)
    {
        if (position.X > 0f || position.Y > 0f)
        {
            _initialPosition = position;
            LastPosition = position;
        }

        if (size.X > 0f && size.Y > 0f)
        {
            _initialSize = size;
            LastSize = size;
        }
    }

    public override void DrawContent(IViewport viewport)
    {
        LastPosition = ImGui.GetWindowPos();
        LastSize = ImGui.GetWindowSize();

        if (!_host.TryGetFrame(out TelemetrySnapshot? snapshot, out OverlayConfig? config, out double dt)
            || snapshot is null || config is null)
        {
            ImGui.TextDisabled("Waiting for telemetry."u8);
            return;
        }

        if (!snapshot.HasVehicle)
        {
            ImGui.TextDisabled("No vehicle."u8);
            return;
        }

        PanelContext context = new(ImGui.GetWindowDrawList(), snapshot, config, dt);

        float2 size = _panel.Measure(in context) * config.Scale;
        if (size.X <= 0f || size.Y <= 0f)
        {
            return;
        }

        float2 origin = ImGui.GetCursorScreenPos();
        _panel.Draw(in context, origin, size);

        ImGui.Dummy(in size);
    }

    public void ResetPanel() => _panel.Reset();
}
