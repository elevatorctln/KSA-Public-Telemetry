using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public static class OverlayStyle
{
    public const uint PanelBackground = 0x99080A0Cu;
    public const uint PanelBorder     = 0x00000000u;
    public const uint Hairline        = 0x59FFFFFFu;
    public const uint HairlineFaint   = 0x26FFFFFFu;

    public const uint TextPrimary = 0xFFFFFFFFu;
    public const uint TextMuted   = 0xFFB9C4CCu;
    public const uint TextDim     = 0xFF7A868Fu;
    public const uint TextAccent  = 0xFFF5D48Cu;
    public const uint TextShadow  = 0x8C000000u;

    public const uint EngineNominal   = 0xFFFFFFFFu; // white
    public const uint EngineThrottled = 0xFF5CC8FFu; // amber
    public const uint EngineArmed     = 0xFF6E5F4Fu; // slate
    public const uint EngineStarved   = 0xFF4A4AF5u; // red
    public const uint EngineInactive  = 0xFF6E5F4Fu; // dark grey
    public const uint EngineOutline   = 0xB3000000u;

    public const uint BarTrack       = 0x59202428u;
    public const uint BarDefaultFill = 0xFFF5D48Cu;
    public const uint MaxQMarker     = 0xFF5CC8FFu;
    public const uint ArcFill  = 0xFFFFFFFFu;
    public const uint ArcTrack = 0x4DFFFFFFu;
    public const uint ShelfBottom = 0xB3000000u;
    public const uint ShelfTop    = 0x00000000u;
    public const uint GaugePlate = 0x66101418u;
    public const uint GaugeRim = 0x73FFFFFFu;
    public const uint TimelinePast   = 0xE6FFFFFFu;
    public const uint TimelineFuture = 0x59FFFFFFu;
    public const uint Caution = 0xFF5CC8FFu;
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
}
