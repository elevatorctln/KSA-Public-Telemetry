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

    private const int FilletSamples = 12;

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

        float filletDrop = Drop(radius);
        float runEnd = height <= filletDrop
            ? MathF.Sqrt(2f * radius * height / slope)
            : radius * 0.5f + height / slope;

        Span<float2> points = stackalloc float2[FilletSamples + 4];
        int n = 0;

        points[n++] = new float2(edgeX, bottom);
        points[n++] = new float2(edgeX, top);

        float filletEnd = MathF.Min(radius, runEnd);
        for (int i = 0; i <= FilletSamples; i++)
        {
            float past = filletEnd * i / FilletSamples;
            points[n++] = new float2(
                edgeX + direction * (flatWidth + past),
                MathF.Min(top + Drop(past), bottom));
        }

        if (runEnd > filletEnd)
        {
            points[n++] = new float2(edgeX + direction * (flatWidth + runEnd), bottom);
        }

        if (!onLeft)
        {
            points[..n].Reverse();
        }

        int vertexStart = drawList.VtxBuffer.Count;
        drawList.AddConvexPolyFilled(points[..n], OverlayStyle.WithOpacity(OverlayStyle.ShelfBox, opacity));
        int vertexEnd = drawList.VtxBuffer.Count;

        float floorFraction = Math.Clamp(Tuning.ShelfFloorFraction, 0f, 1f);
        Span<ImDrawVert> vertices = drawList.VtxBuffer.Span;

        for (int i = vertexStart; i < vertexEnd; i++)
        {
            ref ImDrawVert vertex = ref vertices[i];
            float t = Math.Clamp((vertex.pos.Y - top) / height, 0f, 1f);
            float fade = 1f - t * (1f - floorFraction);
            uint alpha = (uint)(((vertex.col >> 24) & 0xFFu) * fade);
            vertex.col = (vertex.col & 0x00FFFFFFu) | (alpha << 24);
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
        float opacity)
    {
        float span = right - left;
        if (span <= 0f || thickness <= 0f)
        {
            return;
        }

        fadeWidth = Math.Clamp(fadeWidth, 0f, span * 0.5f);

        Span<float> stops = [left, left + fadeWidth, Math.Clamp(splitX, left, right), right - fadeWidth, right];
        stops.Sort();

        float halfThickness = thickness * 0.5f;

        float AlphaAt(float x) => fadeWidth > 0f
            ? Math.Clamp(MathF.Min(x - left, right - x) / fadeWidth, 0f, 1f)
            : 1f;

        for (int i = 0; i + 1 < stops.Length; i++)
        {
            float x0 = stops[i];
            float x1 = stops[i + 1];

            if (x1 - x0 <= 0f)
            {
                continue;
            }

            uint color = (x0 + x1) * 0.5f <= splitX ? beforeColor : afterColor;
            uint c0 = OverlayStyle.WithOpacity(color, opacity * AlphaAt(x0));
            uint c1 = OverlayStyle.WithOpacity(color, opacity * AlphaAt(x1));

            float2 min = new(x0, y - halfThickness);
            float2 max = new(x1, y + halfThickness);

            drawList.AddRectFilledMultiColor(in min, in max, c0, c1, c1, c0);
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

    public static void PushFont(ImFontPtr? font, float sizePixels)
        => ImGui.PushFont(font ?? default, sizePixels);

    public static void PopFont() => ImGui.PopFont();

    public static float2 MeasureWithFont(ImFontPtr? font, float sizePixels, ReadOnlySpan<char> text)
    {
        PushFont(font, sizePixels);
        float2 size = ImGui.CalcTextSize(text);
        PopFont();
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
        PushFont(font, sizePixels);
        float width = ImGui.CalcTextSize(text).X;
        Text(drawList, new float2(centerX - width * 0.5f, y), color, text, opacity);
        PopFont();
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
        PushFont(font, sizePixels);
        Text(drawList, pos, color, text, opacity);
        PopFont();
    }

    private static float DigitCellWidth()
    {
        float widest = 0f;
        Span<char> digit = stackalloc char[1];

        for (char c = '0'; c <= '9'; c++)
        {
            digit[0] = c;
            widest = MathF.Max(widest, ImGui.CalcTextSize(digit).X);
        }

        return widest;
    }

    public static float2 MeasureTabular(ImFontPtr? font, float sizePixels, ReadOnlySpan<char> text)
    {
        PushFont(font, sizePixels);

        float cell = DigitCellWidth();
        float width = 0f;

        for (int i = 0; i < text.Length; i++)
        {
            width += char.IsAsciiDigit(text[i]) ? cell : ImGui.CalcTextSize(text.Slice(i, 1)).X;
        }

        float height = ImGui.CalcTextSize(text).Y;
        PopFont();

        return new float2(width, height);
    }

    public static void TextTabular(
        ImDrawListPtr drawList,
        ImFontPtr? font,
        float sizePixels,
        float2 pos,
        uint color,
        ReadOnlySpan<char> text,
        float opacity)
    {
        PushFont(font, sizePixels);

        float cell = DigitCellWidth();
        float x = pos.X;

        for (int i = 0; i < text.Length; i++)
        {
            ReadOnlySpan<char> glyph = text.Slice(i, 1);
            float advance = ImGui.CalcTextSize(glyph).X;

            if (char.IsAsciiDigit(text[i]))
            {
                Text(drawList, new float2(x + (cell - advance) * 0.5f, pos.Y), color, glyph, opacity);
                x += cell;
            }
            else
            {
                Text(drawList, new float2(x, pos.Y), color, glyph, opacity);
                x += advance;
            }
        }

        PopFont();
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
