using Brutal.ImGuiApi;
using Brutal.Numerics;
using KSATelemetryOverlay.Config;

namespace KSATelemetryOverlay.Rendering;

/// debug window for tuning overlay appearance and animation.
public static class TuningUi
{
    private const float SliderWidth = 150f;

    private static bool _open;
    private static OverlayRenderer? _renderer;

    public static bool IsOpen => _open;

    public static void Bind(OverlayRenderer renderer) => _renderer = renderer;

    public static void SetOpen(bool open) => _open = open;

    public static void Toggle() => _open = !_open;

    public static void Draw()
    {
        if (!_open)
        {
            return;
        }

        ImGui.SetNextWindowSize(new float2(420f, 620f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Overlay tuning"u8, ref _open))
        {
            DrawContent();
        }

        ImGui.End();
    }

    private static void DrawContent()
    {
        bool changed = false;

        if (ImGui.SmallButton("Replay intro"u8))
        {
            _renderer?.ReplayIntro();
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Reset all"u8))
        {
            Tuning.ResetAll();
            changed = true;
        }

        ImGui.SameLine();

        if (ImGui.SmallButton("Copy as source"u8))
        {
            string dump = Tuning.DumpSource();

            Console.WriteLine("[KSATelemetryOverlay] tuning dump:\n" + dump);
            ImGui.SetClipboardText(dump);
        }

        ImGui.TextDisabled("Changes apply live and are saved to config.json."u8);

        if (ImGui.CollapsingHeader("Intro sequence"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.TextDisabled("Stage starts and lengths are in seconds, before the scale."u8);
            changed |= Slider("Time scale", ref Tuning.IntroTimeScale, 0.25f, 3f);
            changed |= Slider("Shelf slide px", ref Tuning.ShelfSlideDistance, 0f, 500f, "%.0f"u8);
            changed |= Slider("Shelf slide lag", ref Tuning.ShelfSlideEase, 0.1f, 2f);

            ImGui.TextDisabled("Gauges nearer the screen edge lead the ones nearer the centre."u8);
            changed |= Slider("Stagger per gauge s", ref Tuning.GaugeStaggerStep, 0f, 0.4f);
            changed |= Slider("Stagger cap s", ref Tuning.GaugeStaggerMax, 0f, 1.5f);

            changed |= Stage("Backdrop",
                ref Tuning.BackdropStart, ref Tuning.BackdropDuration, ref Tuning.BackdropDecay);
            changed |= Stage("Gauges",
                ref Tuning.GaugesStart, ref Tuning.GaugesDuration, ref Tuning.GaugesDecay);
            changed |= Stage("Ring outline",
                ref Tuning.RimStart, ref Tuning.RimDuration, ref Tuning.RimDecay);
            changed |= Stage("Arc sweep",
                ref Tuning.ArcSweepStart, ref Tuning.ArcSweepDuration, ref Tuning.ArcSweepDecay);
            changed |= Stage("Readouts",
                ref Tuning.ReadoutsStart, ref Tuning.ReadoutsDuration, ref Tuning.ReadoutsDecay);
        }

        if (ImGui.CollapsingHeader("Layout"u8))
        {
            ImGui.TextDisabled("Edge gutter above knee gap sits the gauges against the slope."u8);
            changed |= Slider("Edge gutter x", ref Tuning.PanelEdgeMarginX, 0f, 160f, "%.0f"u8);
            changed |= Slider("Edge gutter y", ref Tuning.PanelEdgeMarginY, 0f, 160f, "%.0f"u8);
            changed |= Slider("Menu bar clearance", ref Tuning.MenuBarClearance, 0f, 80f, "%.0f"u8);
            changed |= Slider("Knee gap", ref Tuning.BoxPadding, 0f, 120f, "%.0f"u8);
            changed |= Slider("Panel gap", ref Tuning.PanelGap, 0f, 60f, "%.0f"u8);
        }

        if (ImGui.CollapsingHeader("Backdrop and shelves"u8))
        {
            changed |= Slider("Shelf height", ref Tuning.BoxHeight, 60f, 400f, "%.0f"u8);
            changed |= Slider("Fade height x", ref Tuning.FadeHeightFactor, 0.5f, 2.5f);
            changed |= Slider("Diagonal slope", ref Tuning.DiagonalSlope, 0.1f, 3f);
            changed |= Slider("Corner radius", ref Tuning.BoxCornerRadius, 0f, 150f, "%.0f"u8);
            changed |= Slider("Side margin", ref Tuning.SideMargin, 0f, 40f, "%.0f"u8);
            changed |= Slider("Min flat width", ref Tuning.MinBoxFlatWidth, 0f, 500f, "%.0f"u8);
            changed |= Slider("Gradient floor", ref Tuning.ShelfFloorFraction, 0f, 1f);
        }

        if (ImGui.CollapsingHeader("Engine cluster"u8))
        {
            ImGui.TextDisabled("Layout"u8);
            changed |= Slider("Cluster fill", ref Tuning.ClusterFillFraction, 0.3f, 1.2f);
            changed |= Slider("Neighbour fill", ref Tuning.NeighbourFillFraction, 0.2f, 1.2f);
            changed |= Slider("Envelope margin", ref Tuning.EnvelopeMargin, 0f, 30f, "%.1f"u8);
            changed |= Slider("Single dot radius", ref Tuning.SingleEngineRadius, 0.05f, 1f);
            changed |= Slider("Min dot radius", ref Tuning.MinDotRadius, 0.5f, 15f, "%.1f"u8);

            ImGui.TextDisabled("Pop in"u8);
            changed |= Slider("Dot start scale", ref Tuning.IntroDotStartScale, 0f, 1.5f);
            changed |= Slider("Ring stagger span", ref Tuning.RingStaggerSpan, 0f, 0.95f);
            changed |= Slider("Ring merge margin", ref Tuning.RingMergeTolerance, 0f, 2f);

            ImGui.TextDisabled("State changes"u8);
            changed |= Slider("State fade s", ref Tuning.StateFadeSeconds, 0f, 1f);
            changed |= Slider("State jitter s", ref Tuning.StateJitterSeconds, 0f, 0.5f);
            changed |= Slider("Swap out s", ref Tuning.SwapOutSeconds, 0.02f, 1f);
            changed |= Slider("Swap in s", ref Tuning.SwapInSeconds, 0.02f, 1f);
        }

        if (ImGui.CollapsingHeader("Readouts"u8))
        {
            changed |= Slider("Value width x", ref Tuning.ValueWidthFraction, 0.5f, 3f);
            changed |= Slider("Min value shrink", ref Tuning.MinValueShrink, 0.2f, 1f);
        }

        if (changed)
        {
            ConfigStore.MarkDirty();
        }
    }

    private static bool Stage(string name, ref float start, ref float duration, ref int decay)
    {
        bool changed = false;

        ImGui.TextDisabled(name);
        changed |= Slider($"start##{name}", ref start, 0f, 2f);
        changed |= Slider($"length##{name}", ref duration, 0.05f, 3f);

        ImGui.SetNextItemWidth(SliderWidth);
        changed |= ImGui.SliderInt($"decay##{name}", ref decay, 1, 20);

        return changed;
    }

    private static bool Slider(string label, ref float value, float min, float max)
        => Slider(label, ref value, min, max, "%.3f"u8);

    private static bool Slider(string label, ref float value, float min, float max, ImString format)
    {
        ImGui.SetNextItemWidth(SliderWidth);
        return ImGui.SliderFloat(label, ref value, min, max, format);
    }
}
