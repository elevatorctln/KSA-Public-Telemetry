using System.Reflection;

namespace KSATelemetryOverlay.Rendering;

/// static defaults for the overlay appearance and animation. I got sick of recompiling the mod with every little tweak. 
/// These are editable in-game but must be updated here manually after changed are made since they are only saved in config.
/// mostly a note for myself.
public static class Tuning
{
    // intro sequence animation
    public static float IntroTimeScale = 1.994f;
    public static float BackdropStart = 0.149f;
    public static float BackdropDuration = 0.641f;
    public static int BackdropDecay = 3;
    public static float GaugesStart = 0.255f;
    public static float GaugesDuration = 0.623f;
    public static int GaugesDecay = 3;
    public static float RimStart = 0.32f;
    public static float RimDuration = 0.182f;
    public static int RimDecay = 1;
    public static float ArcSweepStart = 0.657f;
    public static float ArcSweepDuration = 3f;
    public static int ArcSweepDecay = 20;
    public static float ReadoutsStart = 0.49f;
    public static float ReadoutsDuration = 0.56f;
    public static int ReadoutsDecay = 3;

    public static float GaugeStaggerStep = 0.172f;
    public static float GaugeStaggerMax = 0.50f;

    public static float ShelfSlideDistance = 427f;
    public static float ShelfSlideEase = 1.022f;

    // backdrop and shelves
    public static float SideMargin = 0f;
    public static float BoxHeight = 177f;
    public static float FadeHeightFactor = 0.918f;
    public static float DiagonalSlope = 0.908f;
    public static float BoxCornerRadius = 50f;
    public static float MinBoxFlatWidth = 24f;
    public static float ShelfFloorFraction = 0.269f;
    public static float BoxPadding = 14f;

    // panel layout
    public static float MenuBarClearance = 4f;
    public static float PanelEdgeMarginX = 40f;
    public static float PanelEdgeMarginY = 18f;
    public static float PanelGap = 8f;

    // engine diagram
    public static float EnvelopeMargin = 3.4f;
    public static float MinDotRadius = 3.7f;
    public static float NeighbourFillFraction = 0.893f;
    public static float SingleEngineRadius = 0.37f;
    public static float ClusterFillFraction = 0.893f;
    public static float StateFadeSeconds = 0.18f;
    public static float StateJitterSeconds = 0.1f;
    public static float IntroDotStartScale = 0.55f;
    public static float RingStaggerSpan = 0.8f;
    public static float RingMergeTolerance = 0.35f;
    public static float SwapOutSeconds = 0.12f;
    public static float SwapInSeconds = 0.18f;

    // readouts
    public static float ValueWidthFraction = 1.75f;
    public static float MinValueShrink = 0.584f;

    private static readonly FieldInfo[] _fields = typeof(Tuning)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => !f.IsLiteral && (f.FieldType == typeof(float) || f.FieldType == typeof(int)))
        .OrderBy(f => f.Name, StringComparer.Ordinal)
        .ToArray();

    private static readonly Dictionary<string, double> _defaults =
        _fields.ToDictionary(f => f.Name, Read, StringComparer.Ordinal);

    public static IReadOnlyList<string> Names { get; } = _fields.Select(f => f.Name).ToArray();

    public static void ResetAll()
    {
        for (int i = 0; i < _fields.Length; i++)
        {
            Write(_fields[i], _defaults[_fields[i].Name]);
        }
    }

    public static Dictionary<string, double> CaptureOverrides()
    {
        Dictionary<string, double> overrides = new(StringComparer.Ordinal);

        for (int i = 0; i < _fields.Length; i++)
        {
            FieldInfo field = _fields[i];
            double value = Read(field);

            if (value != _defaults[field.Name])
            {
                overrides[field.Name] = value;
            }
        }

        return overrides;
    }

    public static void ApplyOverrides(Dictionary<string, double>? overrides)
    {
        ResetAll();

        if (overrides is null)
        {
            return;
        }

        foreach (KeyValuePair<string, double> entry in overrides)
        {
            FieldInfo? field = Find(entry.Key);

            if (field is not null)
            {
                Write(field, entry.Value);
            }
        }
    }

    public static string DumpSource()
    {
        System.Text.StringBuilder builder = new();

        for (int i = 0; i < _fields.Length; i++)
        {
            FieldInfo field = _fields[i];
            double value = Read(field);
            bool moved = value != _defaults[field.Name];

            builder.Append(field.FieldType == typeof(int)
                ? $"    public static int {field.Name} = {(int)value};"
                : $"    public static float {field.Name} = {value:0.####}f;");

            builder.AppendLine(moved ? "   // changed" : string.Empty);
        }

        return builder.ToString();
    }

    private static double Read(FieldInfo field)
    {
        if (field.FieldType == typeof(int))
        {
            return (int)field.GetValue(null)!;
        }

        return (float)field.GetValue(null)!;
    }

    private static void Write(FieldInfo field, double value)
    {
        if (field.FieldType == typeof(int))
        {
            field.SetValue(null, (int)value);
            return;
        }

        field.SetValue(null, (float)value);
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
