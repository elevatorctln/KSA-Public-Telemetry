using Brutal.ImGuiApi;
using KSA;

namespace KSATelemetryOverlay.Rendering;

public static class OverlayFonts
{
    private const string RegularFile = "Montserrat-Regular.ttf";
    private const string BoldFile = "Montserrat-Bold.ttf";
    private const string FontFolder = "montserrat";

    private static bool _attempted;
    public static ImFontPtr? Numeric { get; private set; }
    public static ImFontPtr? Label { get; private set; }
    public static ImFontPtr? Body { get; private set; }
    public static bool IsLoaded => Numeric.HasValue;
    public const float NumericSize = 30f;
    public const float ClockSize = 44f;
    public const float LabelSize = 19f;
    public const float BodySize = 20f;
    public static void TryLoad(string? modDirectory)
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;

        try
        {
            string? regular = Resolve(modDirectory, RegularFile);
            string? bold = Resolve(modDirectory, BoldFile);

            if (regular is null && bold is null)
            {
                Console.WriteLine(
                    "[KSATelemetryOverlay] Montserrat not found; using the game font.");
                return;
            }

            string numericSource = bold ?? regular!;
            string labelSource = bold ?? regular!;
            string bodySource = regular ?? bold!;

            ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;

            Numeric = Load(atlas, numericSource, ClockSize);
            Label = Load(atlas, labelSource, LabelSize);
            Body = Load(atlas, bodySource, BodySize);

            Console.WriteLine("[KSATelemetryOverlay] loaded Montserrat overlay fonts.");
        }
        catch (Exception ex)
        {
            Numeric = null;
            Label = null;
            Body = null;
            Console.WriteLine(
                $"[TelemetryOverlay] font load failed, using the game font: {ex.Message}");
        }
    }

    private static unsafe ImFontPtr Load(ImFontAtlasPtr atlas, string path, float sizePixels)
    {
        ImFontConfig config = new()
        {
            OversampleH = 3,
            OversampleV = 2,
            PixelSnapH = false,
            GlyphMaxAdvanceX = float.MaxValue,
            RasterizerMultiply = 1f,
            RasterizerDensity = 1f,
            SizePixels = sizePixels,
        };

        return atlas.AddFontFromFileTTF(path, sizePixels, &config, default);
    }
    
    private static string? Resolve(string? modDirectory, string fileName)
    {
        if (!string.IsNullOrEmpty(modDirectory))
        {
            string beside = Path.Combine(modDirectory, FontFolder, fileName);
            if (File.Exists(beside))
            {
                return beside;
            }

            string flat = Path.Combine(modDirectory, fileName);
            if (File.Exists(flat))
            {
                return flat;
            }
        }

        string local = Path.Combine(FontFolder, fileName);
        return File.Exists(local) ? local : null;
    }
}
