using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSATelemetryOverlay.Rendering;

public sealed class MissionClockPanel : IOverlayPanel
{
    private const float Padding = 10f;
    private const float PrefixGap = 6f;

    public string Id => "mission_clock";

    public string DisplayName => "Mission Clock";

    public string? MissionNameOverride { get; set; }

    public bool IsVisible(in PanelContext context)
        => context.Config.ShowMissionClock && context.Snapshot.HasVehicle;

    public float2 Measure(in PanelContext context)
    {
        Span<char> buffer = stackalloc char[16];
        ReadOnlySpan<char> time = Format.MissionTime(buffer, context.Snapshot.MissionElapsedSeconds);

        float2 timeSize = Gfx.MeasureWithFont(OverlayFonts.Numeric, OverlayFonts.NumericSize, time);
        float2 prefixSize = Gfx.MeasureWithFont(OverlayFonts.Body, OverlayFonts.BodySize, "T+".AsSpan());

        ReadOnlySpan<char> name = ResolveMissionName(in context);
        float2 nameSize = Gfx.MeasureWithFont(OverlayFonts.Label, OverlayFonts.LabelSize, name);

        float width = prefixSize.X + PrefixGap + timeSize.X;
        width = MathF.Max(width, nameSize.X) + Padding * 2f;

        float height = timeSize.Y + nameSize.Y + Padding * 2f;

        return new float2(width, height);
    }

    public void Draw(in PanelContext context, float2 origin, float2 size)
    {
        ImDrawListPtr drawList = context.DrawList;
        float opacity = context.Opacity;
        float scale = context.Scale;

        float numericSize = OverlayFonts.NumericSize * scale;
        float bodySize = OverlayFonts.BodySize * scale;
        float labelSize = OverlayFonts.LabelSize * scale;

        Span<char> buffer = stackalloc char[16];
        ReadOnlySpan<char> time = Format.MissionTime(buffer, context.Snapshot.MissionElapsedSeconds);

        ReadOnlySpan<char> prefix = context.Snapshot.HasLiftoff ? "T+".AsSpan() : "T-".AsSpan();

        float2 timeSize = Gfx.MeasureWithFont(OverlayFonts.Numeric, numericSize, time);
        float2 prefixSize = Gfx.MeasureWithFont(OverlayFonts.Body, bodySize, prefix);

        float centerX = origin.X + size.X * 0.5f;
        float groupWidth = prefixSize.X + PrefixGap * scale + timeSize.X;
        float groupLeft = centerX - groupWidth * 0.5f;

        float clockTop = origin.Y + Padding * scale;

        float prefixY = clockTop + (timeSize.Y - prefixSize.Y) * 0.72f;

        uint timeColor = context.Snapshot.IsFrozen
            ? OverlayStyle.TextMuted
            : OverlayStyle.TextPrimary;

        Gfx.TextFont(
            drawList, OverlayFonts.Body, bodySize,
            new float2(groupLeft, prefixY), OverlayStyle.TextMuted, prefix, opacity);

        Gfx.TextFont(
            drawList, OverlayFonts.Numeric, numericSize,
            new float2(groupLeft + prefixSize.X + PrefixGap * scale, clockTop),
            timeColor, time, opacity);

        ReadOnlySpan<char> name = ResolveMissionName(in context);
        float nameY = clockTop + timeSize.Y;

        Gfx.TextCenteredFont(
            drawList, OverlayFonts.Label, labelSize,
            centerX, nameY, OverlayStyle.TextMuted, name, opacity);
    }

    public void Reset()
    {
    }

    private ReadOnlySpan<char> ResolveMissionName(in PanelContext context)
    {
        if (!string.IsNullOrWhiteSpace(MissionNameOverride))
        {
            return MissionNameOverride.AsSpan();
        }

        string vehicle = context.Snapshot.VehicleName;
        return string.IsNullOrWhiteSpace(vehicle) ? "UNNAMED".AsSpan() : vehicle.AsSpan();
    }
}
