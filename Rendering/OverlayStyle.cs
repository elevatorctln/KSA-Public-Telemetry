using KSATelemetryOverlay.Telemetry;

namespace KSATelemetryOverlay.Rendering;

public static class OverlayStyle
{
    public const uint PanelBackground = 0xC0140F0Bu;
    public const uint PanelBorder     = 0x60FFD9A0u;
    public const uint Hairline        = 0x30FFFFFFu;

    public const uint TextPrimary = 0xFFFFFFFFu;
    public const uint TextMuted   = 0xFFA0A0A0u;
    public const uint TextAccent  = 0xFF5FD0FFu;
    public const uint TextShadow  = 0xA0000000u;

    public const uint EngineNominal   = 0xFF64F0A0u; // green
    public const uint EngineThrottled = 0xFF30C8FFu; // amber
    public const uint EngineArmed     = 0xFF60A0C0u; // dim blue
    public const uint EngineStarved   = 0xFF4040FFu; // red
    public const uint EngineInactive  = 0xFF404040u; // grey
    public const uint EngineOutline   = 0xFF101010u;
    public const uint BarTrack       = 0x80202020u;
    public const uint BarDefaultFill = 0xFF9BD6FFu;
    public const uint MaxQMarker     = 0xFF40E0FFu;
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
