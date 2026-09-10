using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class OverlayRenderer
{
    private const float SideMargin = 18f;

    /// <summary>
    /// Height of the dark band at the bottom of the screen.
    ///
    /// Must clear the tallest panel plus its edge margin, otherwise that panel
    /// pokes out above the backdrop. The engine pod is the tallest at 150px.
    /// </summary>
    private const float BandHeight = 150f + SideMargin + 16f;

    /// <summary>How far the band's top edge drops toward each screen edge.</summary>
    private const float ShoulderDrop = 42f;

    /// <summary>Width of each sloping shoulder.</summary>
    private const float ShoulderWidth = 420f;

    private readonly OverlayConfig _config;
    private readonly PanelHost _host = new();

    private readonly EngineClusterPanel _enginePod = new();
    private readonly MissionClockPanel _missionClock = new();
    private readonly TimelinePanel _timeline = new();

    /// <summary>Fast-changing values, pinned to the outer left edge.</summary>
    private readonly ReadoutPanel _primaryReadouts =
        new("readouts_primary", ReadoutKind.Speed, ReadoutKind.Altitude);

    /// <summary>Slower value, grouped with the engine pod on the right.</summary>
    private readonly ReadoutPanel _secondaryReadouts =
        new("readouts_secondary", ReadoutKind.GForce);

    private string _lastVehicle = string.Empty;

    public OverlayRenderer(OverlayConfig config)
    {
        _config = config;

        // Layout follows the reference broadcasts (see timeline/*.png):
        //
        //   [ SPEED ][ ALTITUDE ]  ...  timeline + T+ clock  ...  [ G-FORCE ][ engines ]
        //
        // Fast-changing numbers sit on the outer edges, the clock is centred,
        // and the slower engine/G-force group is kept together on the right.
        // Everything is pinned to the bottom band and only toggles on and off.
        PanelSlot primary = _host.Add(_primaryReadouts, PanelAnchor.BottomLeft);
        primary.StackVertically = false;
        primary.StackHorizontally = true;

        // Clock sits at the bottom centre; the timeline stacks directly above it.
        _host.Add(_missionClock, PanelAnchor.BottomCenter);
        _host.Add(_timeline, PanelAnchor.BottomCenter);

        // Right group runs inward from the edge: engine pod outermost, then
        // G-force beside it. Horizontal stacking keeps them from overlapping.
        PanelSlot pod = _host.Add(_enginePod, PanelAnchor.BottomRight);
        pod.StackVertically = false;
        pod.StackHorizontally = true;

        PanelSlot gforce = _host.Add(_secondaryReadouts, PanelAnchor.BottomRight);
        gforce.StackVertically = false;
        gforce.StackHorizontally = true;
    }

    /// <summary>Mission clock panel, exposed so the mission name can be set.</summary>
    public MissionClockPanel MissionClock => _missionClock;

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
            _host.ResetAll();
        }

        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImDrawListPtr drawList = ImGui.GetForegroundDrawList();

        // Shaped dark band underneath everything, for both look and legibility
        // against bright footage.
        if (_config.ShowBackdrop)
        {
            float scale = _config.Scale;
            Gfx.BottomShelf(
                drawList, viewport.Pos, viewport.Size,
                BandHeight * scale,
                ShoulderDrop * scale,
                MathF.Min(ShoulderWidth * scale, viewport.Size.X * 0.32f),
                _config.Opacity);
        }

        PanelContext context = new(drawList, snapshot, _config, dt);
        _host.DrawAll(in context, viewport.Pos, viewport.Size);

        if (snapshot.IsFrozen)
        {
            DrawSignalLostBanner(drawList, viewport.Pos, viewport.Size);
        }
    }

    /// <summary>
    /// Makes it obvious the numbers on screen are held rather than live.
    /// </summary>
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
