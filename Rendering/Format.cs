namespace KSATelemetryOverlay.Rendering;

public static class Format
{
        private static ReadOnlySpan<char> Write(
        Span<char> buffer, double value, ReadOnlySpan<char> numericFormat, ReadOnlySpan<char> suffix)
    {
        if (!value.TryFormat(buffer, out int written, numericFormat))
        {
            return "--".AsSpan();
        }

        if (suffix.Length > 0 && written + suffix.Length <= buffer.Length)
        {
            suffix.CopyTo(buffer[written..]);
            written += suffix.Length;
        }

        return buffer[..written];
    }

    public static ReadOnlySpan<char> Speed(Span<char> buffer, double metersPerSecond)
        => Math.Abs(metersPerSecond) >= 10_000.0
            ? Write(buffer, metersPerSecond / 1000.0, "N2", " km/s")
            : Write(buffer, metersPerSecond, "N0", " m/s");

    public static ReadOnlySpan<char> Distance(Span<char> buffer, double meters)
    {
        double abs = Math.Abs(meters);
        if (abs >= 1_000_000.0) return Write(buffer, meters / 1000.0, "N0", " km");
        if (abs >= 1_000.0)     return Write(buffer, meters / 1000.0, "N2", " km");
        return Write(buffer, meters, "N0", " m");
    }

    public static ReadOnlySpan<char> Mass(Span<char> buffer, double kilograms)
        => Math.Abs(kilograms) >= 1_000.0
            ? Write(buffer, kilograms / 1000.0, "N1", " t")
            : Write(buffer, kilograms, "N0", " kg");

    public static ReadOnlySpan<char> Pressure(Span<char> buffer, double pascals)
    {
        double abs = Math.Abs(pascals);
        if (abs >= 1_000_000.0) return Write(buffer, pascals / 1_000_000.0, "N2", " MPa");
        if (abs >= 1_000.0)     return Write(buffer, pascals / 1_000.0, "N1", " kPa");
        return Write(buffer, pascals, "N0", " Pa");
    }

    public static ReadOnlySpan<char> Force(Span<char> buffer, double newtons)
    {
        double abs = Math.Abs(newtons);
        if (abs >= 1_000_000.0) return Write(buffer, newtons / 1_000_000.0, "N2", " MN");
        if (abs >= 1_000.0)     return Write(buffer, newtons / 1_000.0, "N1", " kN");
        return Write(buffer, newtons, "N0", " N");
    }

    public static ReadOnlySpan<char> Number(
        Span<char> buffer, double value, ReadOnlySpan<char> numericFormat, ReadOnlySpan<char> suffix = default)
        => Write(buffer, value, numericFormat, suffix);

    public static ReadOnlySpan<char> MissionTime(Span<char> buffer, double seconds)
    {
        if (buffer.Length < 12)
        {
            return "--:--:--".AsSpan();
        }

        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
        {
            seconds = 0.0;
        }

        long total = (long)seconds;
        long hours = total / 3600L;
        int minutes = (int)(total % 3600L / 60L);
        int secs = (int)(total % 60L);

        int written = 0;

        if (hours < 100L)
        {
            buffer[written++] = (char)('0' + (int)(hours / 10L % 10L));
            buffer[written++] = (char)('0' + (int)(hours % 10L));
        }
        else if (!hours.TryFormat(buffer[written..], out int hourChars))
        {
            return "--:--:--".AsSpan();
        }
        else
        {
            written += hourChars;
        }

        buffer[written++] = ':';
        buffer[written++] = (char)('0' + minutes / 10);
        buffer[written++] = (char)('0' + minutes % 10);
        buffer[written++] = ':';
        buffer[written++] = (char)('0' + secs / 10);
        buffer[written++] = (char)('0' + secs % 10);

        return buffer[..written];
    }
}
