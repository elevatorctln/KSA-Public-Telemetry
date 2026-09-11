using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSATelemetryOverlay.Rendering;

public static class Gfx
{
    public static void Panel(ImDrawListPtr drawList, float2 min, float2 max, float opacity, float rounding = 4f)
    {
        drawList.AddRectFilled(in min, in max, OverlayStyle.WithOpacity(OverlayStyle.PanelBackground, opacity), rounding);
    }

    private static void VerticalGradient(
        ImDrawListPtr drawList, float2 min, float2 max, uint topColor, uint bottomColor, float opacity)
    {
        uint top = OverlayStyle.WithOpacity(topColor, opacity);
        uint bottom = OverlayStyle.WithOpacity(bottomColor, opacity);

        drawList.AddRectFilledMultiColor(in min, in max, top, top, bottom, bottom);
    }

    private const float ShelfColumnWidth = 1f;
    private const int FadeStops = 20;
    public static void BottomFade(
        ImDrawListPtr drawList,
        float2 viewportPos,
        float2 viewportSize,
        float height,
        float opacity)
    {
        float bottom = viewportPos.Y + viewportSize.Y;
        float top = bottom - height;

        float left = viewportPos.X;
        float right = viewportPos.X + viewportSize.X;

        if (right <= left || height <= 0f)
        {
            return;
        }

        float topAlpha = ((OverlayStyle.ShelfTop >> 24) & 0xFFu) / 255f;
        float bottomAlpha = ((OverlayStyle.ShelfBottom >> 24) & 0xFFu) / 255f;
        uint Tone(float t)
        {
            float eased = 1f - (1f - t) * (1f - t);
            float a = topAlpha + (bottomAlpha - topAlpha) * eased;
            return OverlayStyle.WithOpacity((OverlayStyle.ShelfBottom & 0x00FFFFFFu) | 0xFF000000u, opacity * a);
        }

        for (int i = 0; i < FadeStops; i++)
        {
            float t0 = i / (float)FadeStops;
            float t1 = (i + 1) / (float)FadeStops;

            float2 min = new(left, top + height * t0);
            float2 max = new(right, top + height * t1);

            uint c0 = Tone(t0);
            uint c1 = Tone(t1);

            drawList.AddRectFilledMultiColor(in min, in max, c0, c0, c1, c1);
        }
    }

    public static void ShelfBox(
        ImDrawListPtr drawList,
        float2 viewportPos,
        float2 viewportSize,
        float height,
        float flatWidth,
        float slope,
        float cornerRadius,
        bool onLeft,
        float opacity,
        float slideOffset = 0f)
    {
        if (height <= 0f || flatWidth <= 0f || slope <= 0f)
        {
            return;
        }

        float bottom = viewportPos.Y + viewportSize.Y;
        float top = bottom - height;
        float direction = onLeft ? 1f : -1f;
        float edgeX = (onLeft ? viewportPos.X : viewportPos.X + viewportSize.X)
            - direction * slideOffset;
        float kneeX = edgeX + direction * flatWidth;

        float radius = MathF.Max(cornerRadius, 0.01f);
        float Drop(float past)
        {
            if (past <= 0f)
            {
                return 0f;
            }

            return past < radius
                ? slope * past * past / (2f * radius)
                : slope * (past - radius * 0.5f);
        }

        uint Tone(float t)
        {
            float f = 1f - Math.Clamp(t, 0f, 1f) * (1f - Tuning.ShelfFloorFraction);
            return OverlayStyle.WithOpacity(OverlayStyle.ShelfBox, opacity * f);
        }

        uint bottomColor = Tone(1f);

        float flatRun = MathF.Floor(flatWidth);

        {
            float snappedTop = MathF.Floor(top);
            float coverage = 1f - (top - snappedTop);

            float fa = edgeX;
            float fb = edgeX + direction * flatRun;
            float fx0 = MathF.Min(fa, fb);
            float fx1 = MathF.Max(fa, fb);

            if (coverage > 0.01f && coverage < 0.99f)
            {
                float2 edgeMin = new(fx0, snappedTop);
                float2 edgeMax = new(fx1, snappedTop + 1f);

                drawList.AddRectFilled(in edgeMin, in edgeMax,
                    OverlayStyle.WithOpacity(OverlayStyle.ShelfBox, opacity * coverage), 0f);
            }

            float fillTop = snappedTop + 1f;
            if (fillTop < bottom)
            {
                uint topColor = Tone((fillTop - top) / height);

                float2 min = new(fx0, fillTop);
                float2 max = new(fx1, bottom);

                drawList.AddRectFilledMultiColor(in min, in max, topColor, topColor, bottomColor, bottomColor);
            }
        }

        float totalRun = flatWidth + height / slope + radius * 0.5f;
        float step = MathF.Max(ShelfColumnWidth, 1f);

        for (float offset = flatRun; offset < totalRun; offset += step)
        {
            float span = MathF.Min(step, totalRun - offset);
            float centre = offset + span * 0.5f;

            float columnTop = top + Drop(centre - flatWidth);
            if (columnTop >= bottom)
            {
                break;
            }

            float xa = edgeX + direction * offset;
            float xb = edgeX + direction * (offset + span);

            float x0 = MathF.Min(xa, xb);
            float x1 = MathF.Max(xa, xb);

            float snapped = MathF.Floor(columnTop);
            float coverage = 1f - (columnTop - snapped);

            if (coverage > 0.01f && coverage < 0.99f)
            {
                float2 edgeMin = new(x0, snapped);
                float2 edgeMax = new(x1, snapped + 1f);

                drawList.AddRectFilled(in edgeMin, in edgeMax,
                    OverlayStyle.WithOpacity(OverlayStyle.ShelfBox, opacity * coverage), 0f);
            }

            float fillTop = snapped + 1f;
            if (fillTop >= bottom)
            {
                continue;
            }

            uint topColor = Tone((fillTop - top) / height);

            float2 min = new(x0, fillTop);
            float2 max = new(x1, bottom);

            drawList.AddRectFilledMultiColor(in min, in max, topColor, topColor, bottomColor, bottomColor);
        }
    }
    public static void FadedTrack(
        ImDrawListPtr drawList,
        float left,
        float right,
        float splitX,
        float y,
        float thickness,
        uint beforeColor,
        uint afterColor,
        float fadeWidth,
        float opacity,
        int segments = 72)
    {
        float span = right - left;
        if (span <= 0f || segments < 1)
        {
            return;
        }

        float step = span / segments;

        for (int i = 0; i < segments; i++)
        {
            float x0 = left + step * i;
            float x1 = x0 + step;
            float centre = (x0 + x1) * 0.5f;

            float distanceToEnd = MathF.Min(centre - left, right - centre);
            float edgeFade = fadeWidth > 0f
                ? Math.Clamp(distanceToEnd / fadeWidth, 0f, 1f)
                : 1f;

            if (edgeFade <= 0f)
            {
                continue;
            }

            float2 a = new(x0, y);
            float2 b = new(x1 + 0.5f, y);

            drawList.AddLine(in a, in b,
                OverlayStyle.WithOpacity(centre <= splitX ? beforeColor : afterColor, opacity * edgeFade),
                thickness);
        }
    }

    public const float SweepStartAngle = -MathF.PI * 0.75f;

    public static void GaugePlate(
        ImDrawListPtr drawList, float2 center, float radius, float opacity,
        bool rim = false, float scale = 1f, float rimPhase = 1f)
    {
        drawList.AddCircleFilled(in center, radius,
            OverlayStyle.WithOpacity(OverlayStyle.GaugePlate, opacity), 48);

        if (!rim || rimPhase <= 0f)
        {
            return;
        }

        float thickness = MathF.Max(1f, scale);
        uint color = OverlayStyle.WithOpacity(OverlayStyle.GaugeRim, opacity);

        if (rimPhase >= 1f)
        {
            drawList.AddCircle(in center, radius, color, 48, thickness);
            return;
        }

        Arc(drawList, center, radius,
            SweepStartAngle, SweepStartAngle + MathF.Tau * rimPhase,
            OverlayStyle.GaugeRim, thickness, opacity);
    }

    public static void ReadoutBrackets(
        ImDrawListPtr drawList, float2 center, float radius, float opacity, float scale)
    {
        const float arcHalfSweep = MathF.PI * 0.28f;
        float thickness = MathF.Max(1f, 1.5f * scale);

        Arc(drawList, center, radius,
            MathF.PI * 1.5f - arcHalfSweep, MathF.PI * 1.5f + arcHalfSweep,
            OverlayStyle.Hairline, thickness, opacity);

        Arc(drawList, center, radius,
            MathF.PI * 0.5f - arcHalfSweep, MathF.PI * 0.5f + arcHalfSweep,
            OverlayStyle.Hairline, thickness, opacity);
    }

    public static void ReadoutText(
        ImDrawListPtr drawList,
        float2 center,
        float radius,
        ReadOnlySpan<char> label,
        ReadOnlySpan<char> value,
        ReadOnlySpan<char> unit,
        uint valueColor,
        float opacity,
        float scale,
        float textScale = 1f)
    {
        float labelSize = OverlayFonts.LabelSize * scale;
        float valueSize = OverlayFonts.NumericSize * scale;

        float2 valueExtent = MeasureWithFont(OverlayFonts.Numeric, valueSize, value);
        float maxValueWidth = radius * Tuning.ValueWidthFraction;

        if (valueExtent.X > maxValueWidth && valueExtent.X > 0f)
        {
            valueSize *= MathF.Max(maxValueWidth / valueExtent.X, Tuning.MinValueShrink);
        }

        labelSize *= textScale;
        valueSize *= textScale;

        valueExtent = MeasureWithFont(OverlayFonts.Numeric, valueSize, value);
        float2 labelExtent = MeasureWithFont(OverlayFonts.Label, labelSize, label);

        float unitSize = labelSize * 0.85f;
        float2 unitExtent = unit.IsEmpty
            ? default
            : MeasureWithFont(OverlayFonts.Label, unitSize, unit);

        float groupHeight = labelExtent.Y + valueExtent.Y + unitExtent.Y;
        float top = center.Y - groupHeight * 0.5f;

        TextCenteredFont(
            drawList, OverlayFonts.Label, labelSize,
            center.X, top, OverlayStyle.TextMuted, label, opacity);

        TextCenteredFont(
            drawList, OverlayFonts.Numeric, valueSize,
            center.X, top + labelExtent.Y, valueColor, value, opacity);

        if (!unit.IsEmpty)
        {
            TextCenteredFont(
                drawList, OverlayFonts.Label, unitSize,
                center.X, top + labelExtent.Y + valueExtent.Y,
                OverlayStyle.TextDim, unit, opacity);
        }
    }

    private static void Text(ImDrawListPtr drawList, float2 pos, uint color, ReadOnlySpan<char> text, float opacity)
    {
        float2 shadowPos = pos + new float2(1f, 1f);
        drawList.AddText(in shadowPos, OverlayStyle.WithOpacity(OverlayStyle.TextShadow, opacity), text);
        drawList.AddText(in pos, OverlayStyle.WithOpacity(color, opacity), text);
    }

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
}
