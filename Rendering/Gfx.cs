using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSATelemetryOverlay.Rendering;

public static class Gfx
{
    public static void Panel(ImDrawListPtr drawList, float2 min, float2 max, float opacity, float rounding = 4f)
    {
        drawList.AddRectFilled(in min, in max, OverlayStyle.WithOpacity(OverlayStyle.PanelBackground, opacity), rounding);
        drawList.AddRect(in min, in max, OverlayStyle.WithOpacity(OverlayStyle.PanelBorder, opacity), rounding, ImDrawFlags.None, 1f);
    }

    public static void Text(ImDrawListPtr drawList, float2 pos, uint color, ReadOnlySpan<char> text, float opacity)
    {
        float2 shadowPos = pos + new float2(1f, 1f);
        drawList.AddText(in shadowPos, OverlayStyle.WithOpacity(OverlayStyle.TextShadow, opacity), text);
        drawList.AddText(in pos, OverlayStyle.WithOpacity(color, opacity), text);
    }
    public static void TextCentered(
        ImDrawListPtr drawList, float centerX, float y, uint color, ReadOnlySpan<char> text, float opacity)
    {
        float width = ImGui.CalcTextSize(text).X;
        Text(drawList, new float2(centerX - width * 0.5f, y), color, text, opacity);
    }

    public static void TextRight(
        ImDrawListPtr drawList, float rightX, float y, uint color, ReadOnlySpan<char> text, float opacity)
    {
        float width = ImGui.CalcTextSize(text).X;
        Text(drawList, new float2(rightX - width, y), color, text, opacity);
    }
    public static void FillBar(
        ImDrawListPtr drawList,
        float2 min,
        float2 size,
        float fraction,
        uint fillColor,
        float opacity,
        float markerFraction = -1f)
    {
        float2 max = min + size;
        drawList.AddRectFilled(in min, in max, OverlayStyle.WithOpacity(OverlayStyle.BarTrack, opacity), 2f);

        float clamped = Math.Clamp(fraction, 0f, 1f);
        if (clamped > 0f)
        {
            float2 fillMax = new(min.X + size.X * clamped, max.Y);
            drawList.AddRectFilled(in min, in fillMax, OverlayStyle.WithOpacity(fillColor, opacity), 2f);
        }

        if (markerFraction >= 0f)
        {
            float markerX = min.X + size.X * Math.Clamp(markerFraction, 0f, 1f);
            float2 markerMin = new(markerX - 1f, min.Y);
            float2 markerMax = new(markerX + 1f, max.Y);
            drawList.AddRectFilled(in markerMin, in markerMax, OverlayStyle.WithOpacity(OverlayStyle.MaxQMarker, opacity));
        }
    }
    public static void TextClipped(
        ImDrawListPtr drawList, float2 pos, uint color, ReadOnlySpan<char> text, float maxWidth, float opacity)
    {
        if (maxWidth <= 0f)
        {
            return;
        }

        if (ImGui.CalcTextSize(text).X <= maxWidth)
        {
            Text(drawList, pos, color, text, opacity);
            return;
        }

        Span<char> buffer = stackalloc char[96];
        int lo = 0;
        int hi = Math.Min(text.Length, buffer.Length - 1);
        int best = 0;

        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            text[..mid].CopyTo(buffer);
            buffer[mid] = Ellipsis;

            if (ImGui.CalcTextSize(buffer[..(mid + 1)]).X <= maxWidth)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (best <= 0)
        {
            return;
        }

        text[..best].CopyTo(buffer);
        buffer[best] = Ellipsis;
        Text(drawList, pos, color, buffer[..(best + 1)], opacity);
    }

    private const char Ellipsis = '…';
    public static void Separator(ImDrawListPtr drawList, float x0, float x1, float y, float opacity)
    {
        float2 a = new(x0, y);
        float2 b = new(x1, y);
        drawList.AddLine(in a, in b, OverlayStyle.WithOpacity(OverlayStyle.Hairline, opacity), 1f);
    }
}
