using System.Globalization;
using System.Reflection;
using KSATelemetryOverlay.Rendering;

namespace KSATelemetryOverlay.Config;
public static class OverlayPalette
{
    private static readonly FieldInfo[] _fields = typeof(OverlayStyle)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(uint) && !f.IsLiteral)
        .OrderBy(f => f.Name, StringComparer.Ordinal)
        .ToArray();

    private static readonly Dictionary<string, uint> _defaults = typeof(OverlayStyle.Defaults)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(uint))
        .ToDictionary(f => f.Name, f => (uint)f.GetRawConstantValue()!, StringComparer.Ordinal);

    public static IReadOnlyList<string> Names { get; } =
        _fields.Select(f => f.Name).ToArray();

    public static uint Get(string name)
    {
        FieldInfo? field = Find(name);
        return field is null ? 0u : (uint)field.GetValue(null)!;
    }

    public static void Set(string name, uint value)
        => Find(name)?.SetValue(null, value);

    public static bool IsDefault(string name)
        => _defaults.TryGetValue(name, out uint d) && Get(name) == d;

    public static void Reset(string name)
    {
        if (_defaults.TryGetValue(name, out uint d))
        {
            Set(name, d);
        }
    }

    public static void ResetAll()
    {
        foreach (KeyValuePair<string, uint> entry in _defaults)
        {
            Set(entry.Key, entry.Value);
        }
    }
    public static Dictionary<string, string> CaptureOverrides()
    {
        Dictionary<string, string> overrides = new(StringComparer.Ordinal);

        foreach (string name in Names)
        {
            if (!IsDefault(name))
            {
                overrides[name] = ToHex(Get(name));
            }
        }

        return overrides;
    }

    public static void ApplyOverrides(Dictionary<string, string>? overrides)
    {
        ResetAll();

        if (overrides is null)
        {
            return;
        }

        foreach (KeyValuePair<string, string> entry in overrides)
        {
            if (Find(entry.Key) is not null && TryParseHex(entry.Value, out uint packed))
            {
                Set(entry.Key, packed);
            }
        }
    }

    public static string ToHex(uint packed)
    {
        OverlayStyle.Unpack(packed, out float r, out float g, out float b, out byte alpha);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"#{(byte)(r * 255f):X2}{(byte)(g * 255f):X2}{(byte)(b * 255f):X2}{alpha:X2}");
    }

    public static bool TryParseHex(string? text, out uint packed)
    {
        packed = 0u;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.Length != 6 && span.Length != 8)
        {
            return false;
        }

        if (!byte.TryParse(span[0..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(span[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(span[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            return false;
        }

        byte a = 0xFF;
        if (span.Length == 8
            && !byte.TryParse(span[6..8], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a))
        {
            return false;
        }

        packed = ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        return true;
    }

    private static FieldInfo? Find(string name)
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            if (string.Equals(_fields[i].Name, name, StringComparison.Ordinal))
            {
                return _fields[i];
            }
        }

        return null;
    }
}
