using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

/// <summary>
/// The live overlay palette.
///
/// These are mutable statics rather than constants because the palette is
/// user-editable at runtime (see <see cref="Palette"/> and the settings menu).
/// There is exactly one overlay per process, so a static palette keeps every
/// draw helper in <see cref="Gfx"/> free of theme plumbing.
///
/// Colours are packed the way ImGui expects: 0xAABBGGRR.
/// </summary>
public static class OverlayStyle
{
    public static uint PanelBackground = Defaults.PanelBackground;
    public static uint Hairline        = Defaults.Hairline;

    public static uint TextPrimary = Defaults.TextPrimary;
    public static uint TextMuted   = Defaults.TextMuted;
    public static uint TextDim     = Defaults.TextDim;
    public static uint TextShadow  = Defaults.TextShadow;

    public static uint EngineNominal   = Defaults.EngineNominal;
    public static uint EngineThrottled = Defaults.EngineThrottled;
    public static uint EngineArmed     = Defaults.EngineArmed;
    public static uint EngineStarved   = Defaults.EngineStarved;
    public static uint EngineInactive  = Defaults.EngineInactive;

    public static uint ArcFill  = Defaults.ArcFill;
    public static uint ArcTrack = Defaults.ArcTrack;

    public static uint ShelfBottom = Defaults.ShelfBottom;
    public static uint ShelfTop    = Defaults.ShelfTop;
    public static uint ShelfBox    = Defaults.ShelfBox;

    public static uint GaugePlate = Defaults.GaugePlate;
    public static uint GaugeRim   = Defaults.GaugeRim;

    public static uint TimelinePast   = Defaults.TimelinePast;
    public static uint TimelineFuture = Defaults.TimelineFuture;

    public static uint Caution = Defaults.Caution;

    /// <summary>The shipped palette, and the target of a "reset colours" action.</summary>
    public static class Defaults
    {
        public const uint PanelBackground = 0x99080A0Cu;
        public const uint Hairline        = 0x59FFFFFFu;

        public const uint TextPrimary = 0xFFFFFFFFu;
        public const uint TextMuted   = 0xFFB9C4CCu;
        public const uint TextDim     = 0xFF7A868Fu;
        public const uint TextShadow  = 0x8C000000u;

        public const uint EngineNominal   = 0xFFFFFFFFu; // white
        public const uint EngineThrottled = 0xFF5CC8FFu; // amber
        public const uint EngineArmed     = 0xFF4F4A46u; // dark grey
        public const uint EngineStarved   = 0xFF4A4AF5u; // red
        public const uint EngineInactive  = 0xFF4F4A46u; // dark grey

        public const uint ArcFill  = 0xFFFFFFFFu;
        public const uint ArcTrack = 0x4DFFFFFFu;

        /// <summary>Deepest tone of the middle fade, at the bottom of the screen.</summary>
        public const uint ShelfBottom = 0xB0000000u;
        /// <summary>
        /// Tone at the very top of the fade. Transparent on purpose: any nonzero
        /// value draws a hard horizontal line right across the screen where the
        /// fade begins, which fights the boxes' own edges. The curve rises
        /// steeply just below this, so the onset is quick without being a line.
        /// </summary>
        public const uint ShelfTop    = 0x00000000u;

        /// <summary>
        /// Tone at the top edge of a readout box. This is the crisp step that
        /// makes the raised ends read as panels rather than as more haze.
        /// </summary>
        public const uint ShelfBox    = 0x66000000u;

        /// <summary>Extra darkening for the raised readout boxes at each end.</summary>


        public const uint GaugePlate = 0x66101418u;
        public const uint GaugeRim   = 0x73FFFFFFu;

        public const uint TimelinePast   = 0xE6FFFFFFu;
        public const uint TimelineFuture = 0x59FFFFFFu;

        public const uint Caution = 0xFF5CC8FFu;
    }

    public static uint ColorFor(EngineStatus status) => status switch
    {
        EngineStatus.Nominal   => EngineNominal,
        EngineStatus.Throttled => EngineThrottled,
        EngineStatus.Armed     => EngineArmed,
        EngineStatus.Starved   => EngineStarved,
        _                      => EngineInactive,
    };

    public static uint WithOpacity(uint color, float opacity)
    {
        uint alpha = (color >> 24) & 0xFFu;
        uint scaled = (uint)Math.Clamp(alpha * opacity, 0f, 255f);
        return (color & 0x00FFFFFFu) | (scaled << 24);
    }

    public static uint Pack(float r, float g, float b, float a)
    {
        uint ri = (uint)Math.Clamp(r * 255f, 0f, 255f);
        uint gi = (uint)Math.Clamp(g * 255f, 0f, 255f);
        uint bi = (uint)Math.Clamp(b * 255f, 0f, 255f);
        uint ai = (uint)Math.Clamp(a * 255f, 0f, 255f);
        return (ai << 24) | (bi << 16) | (gi << 8) | ri;
    }

    /// <summary>Splits a packed colour into linear 0..1 RGB plus its alpha byte.</summary>
    public static void Unpack(uint color, out float r, out float g, out float b, out byte alpha)
    {
        r = (color & 0xFFu) / 255f;
        g = ((color >> 8) & 0xFFu) / 255f;
        b = ((color >> 16) & 0xFFu) / 255f;
        alpha = (byte)((color >> 24) & 0xFFu);
    }

    /// <summary>Replaces a colour's RGB while keeping its existing alpha.</summary>
    public static uint WithRgb(uint color, float r, float g, float b)
        => (color & 0xFF000000u) | (Pack(r, g, b, 1f) & 0x00FFFFFFu);
}
