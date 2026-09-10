using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class OverlayRenderer
{
    private const float SidePanelWidth = 230f;
    private const float SideMargin = 18f;
    private const float TopMargin = 90f;
    private const float PanelGap = 8f;

    private readonly OverlayConfig _config;
    private readonly EngineDiagramPanel _enginePanel = new();
    private readonly PropellantPanel _propellantPanel = new();
    private readonly TelemetryBarPanel _telemetryBar = new();

    private string _lastVehicle = string.Empty;

    public OverlayRenderer(OverlayConfig config) => _config = config;

    public void Draw(TelemetrySnapshot snapshot, double dt)
    {
        HandleToggleKey();

        if (!_config.Enabled)
        {
            return;
        }

        if (!snapshot.HasVehicle)
        {
            if (_config.ShowStatusWhenIdle)
            {
                DrawIdleStatus();
            }
            return;
        }

        if (_config.HideOnRails && snapshot.OnRails)
        {
            return;
        }

        if (!string.Equals(_lastVehicle, snapshot.VehicleName, StringComparison.Ordinal))
        {
            _lastVehicle = snapshot.VehicleName;
            _telemetryBar.Reset();
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float2 viewportPos = viewport.Pos;
        float2 viewportSize = viewport.Size;

        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

        if (_config.ShowTelemetryBar)
        {
            _telemetryBar.Update(snapshot, dt, _config);
            _telemetryBar.Draw(drawList, snapshot, viewportPos, viewportSize, _config);
        }

        DrawSidePanels(drawList, snapshot, viewportPos, viewportSize);
    }

    private void DrawSidePanels(ImDrawListPtr drawList, TelemetrySnapshot snapshot, float2 viewportPos, float2 viewportSize)
    {
        float width = SidePanelWidth * _config.Scale;
        float2 cursor = new(viewportPos.X + SideMargin, viewportPos.Y + TopMargin);

        if (_config.ShowEngineDiagram)
        {
            float consumed = _enginePanel.Draw(drawList, snapshot, cursor, width, _config);
            cursor.Y += consumed + PanelGap;
        }

        if (_config.ShowPropellants)
        {
            float consumed = _propellantPanel.Draw(drawList, snapshot, cursor, width, _config);
            cursor.Y += consumed + PanelGap;
        }
    }

    private void DrawIdleStatus()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

        float2 min = viewport.Pos + new float2(SideMargin, TopMargin);
        float2 max = min + new float2(SidePanelWidth * _config.Scale, 26f);

        Gfx.Panel(drawList, min, max, _config.Opacity);
        Gfx.Text(drawList, min + new float2(10f, 6f), OverlayStyle.TextMuted,
            "TELEMETRY - NO VEHICLE".AsSpan(), _config.Opacity);
    }

    private void HandleToggleKey()
    {
        if (ImGui.GetIO().WantTextInput)
        {
            return;
        }

        if (ImGui.IsKeyPressed(_config.ToggleKey, repeat: false))
        {
            _config.Enabled = !_config.Enabled;
        }
    }
}
