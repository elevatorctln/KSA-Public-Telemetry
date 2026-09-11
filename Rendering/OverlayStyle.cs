using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

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
    public static uint Critical = Defaults.Critical;
    public static class Defaults
    {
        public const uint PanelBackground = 0x99080A0Cu;
        public const uint Hairline        = 0x59FFFFFFu;
        public const uint TextPrimary = 0xFFFFFFFFu;
        public const uint TextMuted   = 0xFFB9C4CCu;
        public const uint TextDim     = 0xFF7A868Fu;
        public const uint TextShadow  = 0x8C000000u;
        public const uint EngineNominal   = 0xFFFFFFFFu; // white, opaque - running
        public const uint EngineThrottled = 0xFFFFFFFFu; // white, opaque - running
        public const uint EngineArmed     = 0xCC4F4A46; // dark grey, partially transparent - off
        public const uint EngineStarved   = 0xCC4F4A46; // dark grey, partially transparent - off
        public const uint EngineInactive  = 0xCC4F4A46; // dark grey, partially transparent - off
        public const uint ArcFill  = 0xFFFFFFFFu;
        public const uint ArcTrack = 0x4DFFFFFFu;
        public const uint ShelfBottom = 0xB0000000u;
        public const uint ShelfTop    = 0x00000000u;
        public const uint ShelfBox    = 0x66000000u;
        public const uint GaugePlate = 0x66101418u;
        public const uint GaugeRim   = 0x73FFFFFFu;
        public const uint TimelinePast   = 0xE6FFFFFFu;
        public const uint TimelineFuture = 0x59FFFFFFu;
        public const uint Caution = 0xFF5CC8FFu;
        public const uint Critical = 0xFF4A4AF5u;
    }

    public static uint ColorFor(EngineStatus status) => status switch
    {
        EngineStatus.Nominal   => EngineNominal,
        EngineStatus.Throttled => EngineThrottled,
        EngineStatus.Armed     => EngineArmed,
        EngineStatus.Starved   => EngineStarved,
        EngineStatus.Inactive  => EngineInactive,
        _                      => EngineInactive,
    };
    public static uint Lerp(uint from, uint to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        uint Channel(int shift)
        {
            float a = (from >> shift) & 0xFFu;
            float b = (to >> shift) & 0xFFu;
            return (uint)Math.Clamp(a + (b - a) * t, 0f, 255f);
        }

        return Channel(0) | (Channel(8) << 8) | (Channel(16) << 16) | (Channel(24) << 24);
    }

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
    public static void Unpack(uint color, out float r, out float g, out float b, out byte alpha)
    {
        r = (color & 0xFFu) / 255f;
        g = ((color >> 8) & 0xFFu) / 255f;
        b = ((color >> 16) & 0xFFu) / 255f;
        alpha = (byte)((color >> 24) & 0xFFu);
    }
    public static uint WithRgb(uint color, float r, float g, float b)
        => (color & 0xFF000000u) | (Pack(r, g, b, 1f) & 0x00FFFFFFu);
}
