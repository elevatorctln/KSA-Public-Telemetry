using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public sealed class PropellantPanel
{
    private const float PanelPadding = 12f;
    private const float RowSpacing = 6f;
    private const float LabelValueGap = 8f;
    public float Draw(ImDrawListPtr drawList, TelemetrySnapshot snapshot, float2 topLeft, float width, OverlayConfig config)
    {
        float opacity = config.Opacity;
        List<PropellantSample> propellants = snapshot.Propellants;

        float lineHeight = ImGui.GetTextLineHeight();
        float barHeight = MathF.Max(MathF.Round(lineHeight * 0.55f), 6f);
        float labelGap = MathF.Round(lineHeight * 0.25f);
        float rowHeight = lineHeight + labelGap + barHeight + RowSpacing;
        float headerHeight = lineHeight + MathF.Round(lineHeight * 0.5f);

        int rows = Math.Max(propellants.Count, 1);
        float height = PanelPadding + headerHeight + rows * rowHeight + PanelPadding;

        float2 min = topLeft;
        float2 max = topLeft + new float2(width, height);
        Gfx.Panel(drawList, min, max, opacity);

        float headerY = min.Y + PanelPadding;
        Gfx.Text(drawList, new float2(min.X + PanelPadding, headerY), OverlayStyle.TextMuted, "PROPELLANT".AsSpan(), opacity);

        Span<char> buffer = stackalloc char[64];

        // Total remaining propellant, right-aligned in the header.
        ReadOnlySpan<char> totalText = Format.Mass(buffer, snapshot.PropellantMass);
        Gfx.TextRight(drawList, max.X - PanelPadding, headerY, OverlayStyle.TextPrimary, totalText, opacity);

        float y = headerY + headerHeight;

        if (propellants.Count == 0)
        {
            Gfx.Text(drawList, new float2(min.X + PanelPadding, y), OverlayStyle.TextMuted, "NO TANKS".AsSpan(), opacity);
            return height;
        }

        float barWidth = width - PanelPadding * 2f;

        for (int i = 0; i < propellants.Count; i++)
        {
            PropellantSample propellant = propellants[i];

            uint fillColor = propellant.HasColor
                ? OverlayStyle.Pack(propellant.ColorR, propellant.ColorG, propellant.ColorB, 1f)
                : OverlayStyle.BarDefaultFill;

            ReadOnlySpan<char> pct = Format.Number(buffer, propellant.Fraction * 100f, "N0", "%");
            float pctWidth = ImGui.CalcTextSize(pct).X;
            Gfx.TextRight(drawList, max.X - PanelPadding, y, OverlayStyle.TextMuted, pct, opacity);

            float nameLimit = barWidth - pctWidth - LabelValueGap;
            Gfx.TextClipped(drawList, new float2(min.X + PanelPadding, y), OverlayStyle.TextPrimary,
                propellant.Name.AsSpan(), nameLimit, opacity);

            float2 barMin = new(min.X + PanelPadding, y + lineHeight + labelGap);
            Gfx.FillBar(drawList, barMin, new float2(barWidth, barHeight), propellant.Fraction, fillColor, opacity);

            y += rowHeight;
        }

        return height;
    }
}
