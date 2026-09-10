using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSATelemetryOverlay.Rendering;

public static class Gfx
{
    public static void Panel(ImDrawListPtr drawList, float2 min, float2 max, float opacity, float rounding = 4f)
    {
        drawList.AddRectFilled(in min, in max, OverlayStyle.WithOpacity(OverlayStyle.PanelBackground, opacity), rounding);
    }
    public static void VerticalGradient(
        ImDrawListPtr drawList, float2 min, float2 max, uint topColor, uint bottomColor, float opacity)
    {
        uint top = OverlayStyle.WithOpacity(topColor, opacity);
        uint bottom = OverlayStyle.WithOpacity(bottomColor, opacity);

        drawList.AddRectFilledMultiColor(in min, in max, top, top, bottom, bottom);
    }

    public static void BottomShelf(
        ImDrawListPtr drawList,
        float2 viewportPos,
        float2 viewportSize,
        float bandHeight,
        float shoulderDrop,
        float shoulderWidth,
        float opacity)
    {
        float bottom = viewportPos.Y + viewportSize.Y;
        float bandTop = bottom - bandHeight;

        float left = viewportPos.X;
        float right = viewportPos.X + viewportSize.X;

        float2 centreMin = new(left + shoulderWidth, bandTop);
        float2 centreMax = new(right - shoulderWidth, bottom);

        if (centreMax.X > centreMin.X)
        {
            VerticalGradient(drawList, centreMin, centreMax,
                OverlayStyle.ShelfTop, OverlayStyle.ShelfBottom, opacity);
        }

        const int steps = 24;
        float shoulderTop = bandTop + shoulderDrop;

        for (int i = 0; i < steps; i++)
        {
            float t0 = i / (float)steps;
            float t1 = (i + 1) / (float)steps;
            float e0 = t0 * t0;
            float e1 = t1 * t1;

            float lx0 = left + shoulderWidth * (1f - t0);
            float lx1 = left + shoulderWidth * (1f - t1);
            DrawShoulderColumn(drawList, lx1, lx0, bandTop, shoulderTop, bottom, e1, e0, opacity);

            float rx0 = right - shoulderWidth * (1f - t0);
            float rx1 = right - shoulderWidth * (1f - t1);
            DrawShoulderColumn(drawList, rx0, rx1, bandTop, shoulderTop, bottom, e0, e1, opacity);
        }
    }

    private static void DrawShoulderColumn(
        ImDrawListPtr drawList,
        float x0,
        float x1,
        float bandTop,
        float shoulderTop,
        float bottom,
        float tNear,
        float tFar,
        float opacity)
    {
        if (x1 <= x0)
        {
            return;
        }

        float t = (tNear + tFar) * 0.5f;
        float top = bandTop + (shoulderTop - bandTop) * t;

        if (top >= bottom)
        {
            return;
        }

        float edgeFade = 1f - t;

        float2 min = new(x0, top);
        float2 max = new(x1, bottom);

        VerticalGradient(drawList, min, max,
            OverlayStyle.ShelfTop, OverlayStyle.ShelfBottom, opacity * edgeFade);
    }

    public static void GaugePlate(
        ImDrawListPtr drawList, float2 center, float radius, float opacity, bool rim = false, float scale = 1f)
    {
        drawList.AddCircleFilled(in center, radius,
            OverlayStyle.WithOpacity(OverlayStyle.GaugePlate, opacity), 48);

        if (rim)
        {
            drawList.AddCircle(in center, radius,
                OverlayStyle.WithOpacity(OverlayStyle.GaugeRim, opacity), 48, MathF.Max(1f, scale));
        }
    }

    public static float2 ReadoutCapsule(
        ImDrawListPtr drawList,
        float2 center,
        float radius,
        ReadOnlySpan<char> label,
        ReadOnlySpan<char> value,
        uint valueColor,
        float opacity,
        float scale)
    {
        const float arcHalfSweep = MathF.PI * 0.28f;
        float thickness = MathF.Max(1f, 1.5f * scale);

        Arc(drawList, center, radius,
            MathF.PI * 1.5f - arcHalfSweep, MathF.PI * 1.5f + arcHalfSweep,
            OverlayStyle.Hairline, thickness, opacity);

        Arc(drawList, center, radius,
            MathF.PI * 0.5f - arcHalfSweep, MathF.PI * 0.5f + arcHalfSweep,
            OverlayStyle.Hairline, thickness, opacity);

        float labelSize = OverlayFonts.LabelSize * scale;
        float valueSize = OverlayFonts.NumericSize * scale;

        float2 labelExtent = MeasureWithFont(OverlayFonts.Label, labelSize, label);
        float2 valueExtent = MeasureWithFont(OverlayFonts.Numeric, valueSize, value);

        float groupHeight = labelExtent.Y + valueExtent.Y;
        float top = center.Y - groupHeight * 0.5f;

        TextCenteredFont(
            drawList, OverlayFonts.Label, labelSize,
            center.X, top, OverlayStyle.TextMuted, label, opacity);

        TextCenteredFont(
            drawList, OverlayFonts.Numeric, valueSize,
            center.X, top + labelExtent.Y, valueColor, value, opacity);

        return center;
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
    public static bool PushFont(ImFontPtr? font, float sizePixels)
    {
        if (!font.HasValue)
        {
            return false;
        }

        ImGui.PushFont(font.Value, sizePixels);
        return true;
    }

    public static void PopFont(bool pushed)
    {
        if (pushed)
        {
            ImGui.PopFont();
        }
    }

    public static float2 MeasureWithFont(ImFontPtr? font, float sizePixels, ReadOnlySpan<char> text)
    {
        bool pushed = PushFont(font, sizePixels);
        float2 size = ImGui.CalcTextSize(text);
        PopFont(pushed);
        return size;
    }

    public static void TextCenteredFont(
        ImDrawListPtr drawList,
        ImFontPtr? font,
        float sizePixels,
        float centerX,
        float y,
        uint color,
        ReadOnlySpan<char> text,
        float opacity)
    {
        bool pushed = PushFont(font, sizePixels);
        float width = ImGui.CalcTextSize(text).X;
        Text(drawList, new float2(centerX - width * 0.5f, y), color, text, opacity);
        PopFont(pushed);
    }

    public static void TextFont(
        ImDrawListPtr drawList,
        ImFontPtr? font,
        float sizePixels,
        float2 pos,
        uint color,
        ReadOnlySpan<char> text,
        float opacity)
    {
        bool pushed = PushFont(font, sizePixels);
        Text(drawList, pos, color, text, opacity);
        PopFont(pushed);
    }

    public static void Arc(
        ImDrawListPtr drawList,
        float2 center,
        float radius,
        float startAngle,
        float endAngle,
        uint color,
        float thickness,
        float opacity,
        int segments = 0)
    {
        if (radius <= 0f || Math.Abs(endAngle - startAngle) < 1e-4f)
        {
            return;
        }

        const float quarterTurn = MathF.PI * 0.5f;
        float a0 = startAngle - quarterTurn;
        float a1 = endAngle - quarterTurn;

        if (segments <= 0)
        {
            float sweep = Math.Abs(a1 - a0);
            segments = Math.Clamp((int)(sweep / (MathF.PI / 45f)), 4, 128);
        }

        drawList.PathClear();
        drawList.PathArcTo(in center, radius, a0, a1, segments);
        drawList.PathStroke(OverlayStyle.WithOpacity(color, opacity), ImDrawFlags.None, thickness);
    }
    public static void Separator(ImDrawListPtr drawList, float x0, float x1, float y, float opacity)
    {
        float2 a = new(x0, y);
        float2 b = new(x1, y);
        drawList.AddLine(in a, in b, OverlayStyle.WithOpacity(OverlayStyle.Hairline, opacity), 1f);
    }
}
