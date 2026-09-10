using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class TelemetryBarPanel
{
    private const float PanelPadding = 18f;
    private const float BarWidth = 660f;
    private const float BottomMargin = 28f;
    private const float FieldGap = 14f;
    private double _speed;
    private double _altitude;
    private double _gLoad;
    private bool _initialised;

    public void Update(TelemetrySnapshot snapshot, double dt, OverlayConfig config)
    {
        if (!_initialised)
        {
            _speed = snapshot.SurfaceSpeed;
            _altitude = snapshot.Altitude;
            _gLoad = snapshot.GLoad;
            _initialised = true;
            return;
        }

        double tau = Math.Max(config.SmoothingSeconds, 1e-4);
        double alpha = 1.0 - Math.Exp(-dt / tau);

        _speed += (snapshot.SurfaceSpeed - _speed) * alpha;
        _altitude += (snapshot.Altitude - _altitude) * alpha;
        _gLoad += (snapshot.GLoad - _gLoad) * alpha;
    }

    public void Reset() => _initialised = false;

    public void Draw(ImDrawListPtr drawList, TelemetrySnapshot snapshot, float2 viewportPos, float2 viewportSize, OverlayConfig config)
    {
        float opacity = config.Opacity;
        float width = BarWidth * config.Scale;
        float lineHeight = ImGui.GetTextLineHeight();
        float labelGap = MathF.Round(lineHeight * 0.2f);
        float rowHeight = lineHeight + labelGap + lineHeight;
        float rowGap = MathF.Round(lineHeight * 0.7f);
        float height = PanelPadding + rowHeight + rowGap + rowHeight + PanelPadding;

        float2 min = new(
            viewportPos.X + (viewportSize.X - width) * 0.5f,
            viewportPos.Y + viewportSize.Y - height - BottomMargin);
        float2 max = min + new float2(width, height);

        Gfx.Panel(drawList, min, max, opacity);

        Span<char> buffer = stackalloc char[64];

        float quarter = width * 0.25f;
        float primaryY = min.Y + PanelPadding;
        float primaryValueY = primaryY + lineHeight + labelGap;

        Gfx.TextCentered(drawList, min.X + quarter, primaryY, OverlayStyle.TextMuted, "SPEED".AsSpan(), opacity);
        Gfx.TextCentered(drawList, min.X + quarter, primaryValueY,
            OverlayStyle.TextPrimary, Format.Speed(buffer, _speed), opacity);

        Gfx.TextCentered(drawList, min.X + quarter * 3f, primaryY, OverlayStyle.TextMuted, "ALTITUDE".AsSpan(), opacity);
        Gfx.TextCentered(drawList, min.X + quarter * 3f, primaryValueY,
            OverlayStyle.TextPrimary, Format.Distance(buffer, _altitude), opacity);

        float2 divTop = new(min.X + width * 0.5f, primaryY);
        float2 divBottom = new(min.X + width * 0.5f, primaryY + rowHeight);
        drawList.AddLine(in divTop, in divBottom, OverlayStyle.WithOpacity(OverlayStyle.Hairline, opacity), 1f);

        float secondaryY = primaryY + rowHeight + rowGap;
        Gfx.Separator(drawList, min.X + PanelPadding, max.X - PanelPadding, secondaryY - rowGap * 0.5f, opacity);

        float fieldWidth = (width - PanelPadding * 2f) / 4f;
        float fieldX = min.X + PanelPadding;

        uint gColor = _gLoad >= 3.0 ? OverlayStyle.EngineThrottled : OverlayStyle.TextPrimary;
        DrawField(drawList, fieldX, secondaryY, fieldWidth, lineHeight, labelGap, "G-LOAD", Format.Number(buffer, _gLoad, "N2", " g"), gColor, opacity);
        fieldX += fieldWidth;

        DrawField(drawList, fieldX, secondaryY, fieldWidth, lineHeight, labelGap, "THRUST", Format.Force(buffer, snapshot.Thrust), OverlayStyle.TextPrimary, opacity);
        fieldX += fieldWidth;

        DrawField(drawList, fieldX, secondaryY, fieldWidth, lineHeight, labelGap, "T/W", Format.Number(buffer, snapshot.ThrustToWeight, "N2"), OverlayStyle.TextPrimary, opacity);
        fieldX += fieldWidth;

        bool pastMaxQ = snapshot.MaxDynamicPressure > 0f && snapshot.DynamicPressure < snapshot.MaxDynamicPressure * 0.98f;
        uint qColor = pastMaxQ ? OverlayStyle.TextMuted : OverlayStyle.TextAccent;
        DrawField(drawList, fieldX, secondaryY, fieldWidth, lineHeight, labelGap, "DYN PRESS", Format.Pressure(buffer, snapshot.DynamicPressure), qColor, opacity);
    }

    private static void DrawField(
        ImDrawListPtr drawList, float x, float y, float width, float lineHeight, float labelGap,
        ReadOnlySpan<char> label, ReadOnlySpan<char> value, uint valueColor, float opacity)
    {
        float centerX = x + width * 0.5f;
        Gfx.TextCentered(drawList, centerX, y, OverlayStyle.TextMuted, label, opacity);
        Gfx.TextCentered(drawList, centerX, y + lineHeight + labelGap, valueColor, value, opacity);
    }
}
