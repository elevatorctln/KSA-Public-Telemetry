using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Internal;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class OverlayRenderer
{
    private readonly OverlayConfig _config;
    private PanelHost _host = new();
    private readonly FlightUiController _flightUi = new();
    private readonly IntroAnimator _intro = new();
    private bool _wasVisible;
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
    public void ReplayIntro() => _intro.Restart();

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

        if (overlayVisible && !_wasVisible)
        {
            _intro.Restart();
        }

        _wasVisible = overlayVisible;

        if (overlayVisible)
        {
            _intro.Update(dt);
        }

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

        PanelContext context = new(
            drawList, snapshot, _config, dt, _intro.Phases,
            viewport.WorkPos, viewport.WorkSize, _intro);

        if (_config.ShowBackdrop)
        {
            DrawBackdrop(in context, drawList, viewport);
        }

        // Top-anchored panels start below the menu bar. Taking it off the top of the
        // work area leaves the bottom edge exactly where it was, so only the
        // notifications move.
        float topInset = MenuBarInset(viewport);

        _host.DrawAll(
            in context,
            new float2(viewport.WorkPos.X, viewport.WorkPos.Y + topInset),
            new float2(viewport.WorkSize.X, viewport.WorkSize.Y - topInset));

        if (snapshot.IsFrozen)
        {
            DrawSignalLostBanner(drawList, viewport.Pos, viewport.Size);
        }
    }

    /// <summary>
    /// How far down the game's menu bar reaches into the viewport.
    ///
    /// It has to be measured rather than read off the viewport: KSA's bar is a plain
    /// window pinned to the top (Program.cs, "Menu Bar", auto-height) and not an
    /// ImGui main menu bar, so it reserves no work area at all and WorkPos sits level
    /// with Pos. The bar also auto-hides, and its height follows the interface scale,
    /// so a fixed offset would be wrong about as often as it was right.
    ///
    /// Falls back to nothing but the clearance if the window cannot be found, which
    /// is what happens on the first frame and would happen if KSA ever renames it.
    /// </summary>
    private static float MenuBarInset(ImGuiViewportPtr viewport)
    {
        float clearance = Tuning.MenuBarClearance;
        ImGuiWindowPtr bar = ImGui.Internal.FindWindowByName("Menu Bar"u8);

        if (bar.IsNull() || !bar.WasActive)
        {
            return clearance;
        }

        float overlap = bar.Pos.Y + bar.Size.Y - viewport.WorkPos.Y;

        return overlap > 0f ? overlap + clearance : clearance;
    }

    private void DrawBackdrop(in PanelContext context, ImDrawListPtr drawList, ImGuiViewportPtr viewport)
    {
        float scale = context.Scale;
        float fadeHeight = Tuning.BoxHeight * Tuning.FadeHeightFactor * scale;

        Gfx.BottomFade(drawList, viewport.Pos, viewport.Size, fadeHeight, context.BackdropOpacity);

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

        float remaining = MathF.Pow(
            1f - Math.Clamp(context.Intro.Backdrop, 0f, 1f), MathF.Max(Tuning.ShelfSlideEase, 0.01f));

        float slideOffset = remaining * Tuning.ShelfSlideDistance * context.Scale;

        Gfx.ShelfBox(
            drawList, viewport.Pos, viewport.Size,
            Tuning.BoxHeight * context.Scale,
            flatWidth,
            Tuning.DiagonalSlope,
            Tuning.BoxCornerRadius * context.Scale,
            onLeft,
            context.BackdropOpacity,
            slideOffset);
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
            Tuning.PanelEdgeMarginX * scale + groupWidth + Tuning.BoxPadding * scale,
            Tuning.MinBoxFlatWidth * scale);
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
            OverlayStyle.Critical, text, _config.Opacity);
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

        float2 min = new(
            viewport.Pos.X + Tuning.SideMargin * _config.Scale,
            viewport.Pos.Y + viewport.Size.Y - extent.Y - padY * 2f - Tuning.SideMargin * _config.Scale);
        float2 max = min + new float2(extent.X + padX * 2f, extent.Y + padY * 2f);

        Gfx.Panel(drawList, min, max, _config.Opacity);
        Gfx.TextFont(drawList, OverlayFonts.Label, labelSize,
            min + new float2(padX, padY), OverlayStyle.TextDim, text, _config.Opacity);
    }
}
