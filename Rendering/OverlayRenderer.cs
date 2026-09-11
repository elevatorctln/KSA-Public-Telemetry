using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class OverlayRenderer
{
    private const float SideMargin = 5f;
    private const float BoxHeight = 150f + SideMargin + 32f;
    private const float FadeHeight = BoxHeight * 1.12f;
    private const float DiagonalSlope = 0.8f;
    private const float BoxCornerRadius = 50f;
    private const float BoxPadding = 30f;
    private const float MinBoxFlatWidth = 140f;

    private readonly OverlayConfig _config;
    private PanelHost _host = new();

    private readonly FlightUiController _flightUi = new();

    private readonly EngineClusterPanel _enginePod = new();
    private readonly MissionClockPanel _missionClock = new();
    private readonly TimelinePanel _timeline = new();
    private readonly NotificationPanel _notifications = new();

    private string _lastVehicle = string.Empty;
    private int _builtRevision = -1;

    public OverlayRenderer(OverlayConfig config)
    {
        _config = config;
        RebuildPanels();
    }

    public void RestoreFlightUi() => _flightUi.Restore();

    private void RebuildPanels()
    {
        _builtRevision = _config.Revision;
        _host = new PanelHost();

        PanelSlot primary = _host.Add(
            new ReadoutPanel("readouts_primary", _config.LeftSlots), PanelAnchor.BottomLeft);
        primary.StackVertically = false;
        primary.StackHorizontally = true;

        _host.Add(_notifications, PanelAnchor.TopLeft);

        _host.Add(_missionClock, PanelAnchor.BottomCenter);
        _host.Add(_timeline, PanelAnchor.BottomCenter);

        PanelSlot pod = _host.Add(_enginePod, PanelAnchor.BottomRight);
        pod.StackVertically = false;
        pod.StackHorizontally = true;

        PanelSlot secondary = _host.Add(
            new ReadoutPanel("readouts_secondary", _config.RightSlots), PanelAnchor.BottomRight);
        secondary.StackVertically = false;
        secondary.StackHorizontally = true;
    }

    public void Draw(TelemetrySnapshot snapshot, double dt)
    {
        if (_builtRevision != _config.Revision)
        {
            RebuildPanels();
        }

        _notifications.Update(snapshot, dt);

        bool overlayVisible = _config.Enabled
            && snapshot.HasVehicle
            && !(_config.HideOnRails && snapshot.OnRails);

        _flightUi.SetHidden(overlayVisible && _config.ReplaceFlightUi);

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
            _host.ResetAll();
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

        PanelContext context = new(drawList, snapshot, _config, dt);

        if (_config.ShowBackdrop)
        {
            DrawBackdrop(in context, drawList, viewport);
        }

        _host.DrawAll(in context, viewport.WorkPos, viewport.WorkSize);

        if (snapshot.IsFrozen)
        {
            DrawSignalLostBanner(drawList, viewport.Pos, viewport.Size);
        }
    }

    private void DrawBackdrop(in PanelContext context, ImDrawListPtr drawList, ImGuiViewportPtr viewport)
    {
        float scale = context.Scale;
        Gfx.BottomFade(drawList, viewport.Pos, viewport.Size, FadeHeight * scale, context.Opacity);

        DrawShelfBox(in context, drawList, viewport, PanelAnchor.BottomLeft, onLeft: true);
        DrawShelfBox(in context, drawList, viewport, PanelAnchor.BottomRight, onLeft: false);
    }

    private void DrawShelfBox(
        in PanelContext context,
        ImDrawListPtr drawList,
        ImGuiViewportPtr viewport,
        PanelAnchor anchor,
        bool onLeft)
    {
        float flatWidth = FlatWidthFor(in context, anchor);

        if (flatWidth <= 0f)
        {
            return;
        }

        Gfx.ShelfBox(
            drawList, viewport.Pos, viewport.Size,
            BoxHeight * context.Scale,
            flatWidth,
            DiagonalSlope,
            BoxCornerRadius * context.Scale,
            onLeft,
            context.Opacity);
    }

    private float FlatWidthFor(in PanelContext context, PanelAnchor anchor)
    {
        float groupWidth = _host.MeasureGroupWidth(in context, anchor);

        if (groupWidth <= 0f)
        {
            return 0f;
        }

        float scale = context.Scale;

        return MathF.Max(
            SideMargin * scale + groupWidth + BoxPadding * scale,
            MinBoxFlatWidth * scale);
    }

    private void DrawSignalLostBanner(ImDrawListPtr drawList, float2 viewportPos, float2 viewportSize)
    {
        ReadOnlySpan<char> text = "SIGNAL LOST".AsSpan();

        float size = OverlayFonts.BodySize * _config.Scale;
        float2 textSize = Gfx.MeasureWithFont(OverlayFonts.Body, size, text);

        float centerX = viewportPos.X + viewportSize.X * 0.5f;
        float y = viewportPos.Y + viewportSize.Y * 0.12f;

        float padX = 14f * _config.Scale;
        float padY = 6f * _config.Scale;

        float2 min = new(centerX - textSize.X * 0.5f - padX, y - padY);
        float2 max = new(centerX + textSize.X * 0.5f + padX, y + textSize.Y + padY);

        Gfx.Panel(drawList, min, max, _config.Opacity);
        Gfx.TextCenteredFont(
            drawList, OverlayFonts.Body, size, centerX, y,
            OverlayStyle.EngineStarved, text, _config.Opacity);
    }

    private void DrawIdleStatus()
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

        ReadOnlySpan<char> text = "TELEMETRY - NO VEHICLE".AsSpan();

        float labelSize = OverlayFonts.LabelSize * _config.Scale;
        float2 extent = Gfx.MeasureWithFont(OverlayFonts.Label, labelSize, text);

        float padX = 12f * _config.Scale;
        float padY = 7f * _config.Scale;

        // Sit in the bottom band, where the real overlay lives.
        float2 min = new(
            viewport.Pos.X + SideMargin * _config.Scale,
            viewport.Pos.Y + viewport.Size.Y - extent.Y - padY * 2f - SideMargin * _config.Scale);
        float2 max = min + new float2(extent.X + padX * 2f, extent.Y + padY * 2f);

        Gfx.Panel(drawList, min, max, _config.Opacity);
        Gfx.TextFont(drawList, OverlayFonts.Label, labelSize,
            min + new float2(padX, padY), OverlayStyle.TextDim, text, _config.Opacity);
    }
}
