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
    private float _signalNotice;
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

        _flightUi.SetHidden(overlayVisible && _config.ReplaceFlightUi && !LayoutWindowOpen());

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

        float topInset = MenuBarInset(viewport);

        _host.DrawAll(
            in context,
            new float2(viewport.WorkPos.X, viewport.WorkPos.Y + topInset),
            new float2(viewport.WorkSize.X, viewport.WorkSize.Y - topInset));

        float fade = (float)(dt / MathF.Max(Tuning.SignalNoticeFadeSeconds, 0.01f));
        _signalNotice = Math.Clamp(_signalNotice + (snapshot.IsFrozen ? fade : -fade), 0f, 1f);

        if (_signalNotice > 0.001f)
        {
            DrawSignalNotice(drawList, viewport, _config.Opacity * _signalNotice);
        }
    }
    private static bool LayoutWindowOpen()
    {
        ImGuiWindowPtr window = ImGui.Internal.FindWindowByName("LAYOUTS###KSA.LayoutSaves+LayoutSavesWindow_"u8);
        return !window.IsNull() && window.WasActive;
    }

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

    private void DrawSignalNotice(ImDrawListPtr drawList, ImGuiViewportPtr viewport, float alpha)
    {
        ReadOnlySpan<char> text = "Please wait for acquisition of signal".AsSpan();

        float scale = _config.Scale;
        float size = OverlayFonts.BodySize * scale;
        float2 extent = Gfx.MeasureWithFont(OverlayFonts.Body, size, text);
        float padX = NotificationPanel.BannerPadX * scale;
        float padY = NotificationPanel.BannerPadY * scale;
        float notch = NotificationPanel.BannerNotch * scale;
        float width = padX * 2f + notch + extent.X;
        float height = extent.Y + padY * 2f;
        float left = viewport.Pos.X + Tuning.PanelEdgeMarginX * scale;
        float bottom = viewport.Pos.Y + viewport.Size.Y
            - (Tuning.BoxHeight + Tuning.SignalNoticeGap) * scale;
        float top = bottom - height;

        Span<float2> shape =
        [
            new(left, top),
            new(left + width, top),
            new(left + width - notch, bottom),
            new(left, bottom),
        ];

        drawList.AddConvexPolyFilled(
            shape, OverlayStyle.WithOpacity(OverlayStyle.PanelBackground, alpha));

        Gfx.TextFont(
            drawList, OverlayFonts.Body, size,
            new float2(left + padX, top + padY), OverlayStyle.TextMuted, text, alpha);
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
            viewport.Pos.X + Tuning.PanelEdgeMarginX * _config.Scale,
            viewport.Pos.Y + viewport.Size.Y - extent.Y - padY * 2f - Tuning.PanelEdgeMarginY * _config.Scale);
        float2 max = min + new float2(extent.X + padX * 2f, extent.Y + padY * 2f);

        Gfx.Panel(drawList, min, max, _config.Opacity);
        Gfx.TextFont(drawList, OverlayFonts.Label, labelSize,
            min + new float2(padX, padY), OverlayStyle.TextDim, text, _config.Opacity);
    }
}
