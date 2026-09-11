using Brutal.ImGuiApi;
using Brutal.Numerics;

namespace KSATelemetryOverlay.Rendering;

public sealed class PanelSlot(IOverlayPanel panel, PanelAnchor anchor)
{
    public IOverlayPanel Panel { get; } = panel;
    public PanelAnchor Anchor { get; set; } = anchor;
    public float2 Offset { get; set; }
    public bool StackVertically { get; set; } = true;
    public bool StackHorizontally { get; set; }
}

public sealed class PanelHost
{
    private readonly List<PanelSlot> _slots = [];
    private readonly Dictionary<PanelAnchor, float> _stackOffsets = [];
    private readonly Dictionary<PanelAnchor, float> _rowOffsets = [];
    public IReadOnlyList<PanelSlot> Slots => _slots;
    public PanelSlot Add(IOverlayPanel panel, PanelAnchor anchor)
    {
        PanelSlot slot = new(panel, anchor);
        _slots.Add(slot);
        return slot;
    }

    public float MeasureGroupWidth(in PanelContext context, PanelAnchor anchor)
    {
        float scale = context.Scale;
        float gap = Tuning.PanelGap * scale;
        float total = 0f;

        for (int i = 0; i < _slots.Count; i++)
        {
            PanelSlot slot = _slots[i];

            if (slot.Anchor != anchor || !slot.StackHorizontally || !slot.Panel.IsVisible(in context))
            {
                continue;
            }

            float width = slot.Panel.Measure(in context).X * scale;
            if (width <= 0f)
            {
                continue;
            }

            total += total > 0f ? width + gap : width;
        }

        return total;
    }

    public void ResetAll()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].Panel.Reset();
        }
    }

    public void DrawAll(in PanelContext context, float2 viewportPos, float2 viewportSize)
    {
        _stackOffsets.Clear();
        _rowOffsets.Clear();

        float scale = context.Scale;
        float marginX = Tuning.PanelEdgeMarginX * scale;
        float marginY = Tuning.PanelEdgeMarginY * scale;
        float gap = Tuning.PanelGap * scale;

        for (int i = 0; i < _slots.Count; i++)
        {
            PanelSlot slot = _slots[i];
            IOverlayPanel panel = slot.Panel;

            if (!panel.IsVisible(in context))
            {
                continue;
            }

            float2 size = panel.Measure(in context) * scale;
            if (size.X <= 0f || size.Y <= 0f)
            {
                continue;
            }

            _stackOffsets.TryGetValue(slot.Anchor, out float stacked);
            _rowOffsets.TryGetValue(slot.Anchor, out float row);

            float2 origin = ResolveOrigin(
                slot, size, viewportPos, viewportSize, marginX, marginY, scale, stacked, row);

            panel.Draw(in context, origin, size);

            if (slot.StackHorizontally)
            {
                _rowOffsets[slot.Anchor] = row + size.X + gap;
            }
            else if (slot.StackVertically)
            {
                _stackOffsets[slot.Anchor] = stacked + size.Y + gap;
            }
        }
    }

    private static float2 ResolveOrigin(
        PanelSlot slot,
        float2 size,
        float2 viewportPos,
        float2 viewportSize,
        float marginX,
        float marginY,
        float scale,
        float stacked,
        float row)
    {
        float left = viewportPos.X + marginX + row;
        float centerX = viewportPos.X + (viewportSize.X - size.X) * 0.5f;
        float right = viewportPos.X + viewportSize.X - size.X - marginX - row;

        float top = viewportPos.Y + marginY;
        float bottom = viewportPos.Y + viewportSize.Y - size.Y - marginY;

        float2 offset = slot.Offset * scale;

        return slot.Anchor switch
        {
            PanelAnchor.TopLeft      => new float2(left,    top    + stacked) + offset,
            PanelAnchor.TopCenter    => new float2(centerX, top    + stacked) + offset,
            PanelAnchor.TopRight     => new float2(right,   top    + stacked) + offset,
            PanelAnchor.BottomLeft   => new float2(left,    bottom - stacked) + offset,
            PanelAnchor.BottomCenter => new float2(centerX, bottom - stacked) + offset,
            PanelAnchor.BottomRight  => new float2(right,   bottom - stacked) + offset,
            _                        => new float2(left,    top    + stacked) + offset,
        };
    }
}
