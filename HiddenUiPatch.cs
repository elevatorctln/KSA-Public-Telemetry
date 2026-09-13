using System.Reflection;
using KSA;

namespace KSATelemetryOverlay;

/// This is a patch dedicated to allowing the overlay to draw while the game's UI is hidden fia the F2 keybind.
/// This seems like the type of things that might break things, as far as I can tell it works fine but it can be
/// toggled in settings, and if it fails it only disables this feature and the mod can continue.
/// (thanks Goddchen!)
public static class HiddenUiPatch
{
    private const string TargetMethod = "DrawFps";
    private static Action<double>? _draw;
    private static double _lastDelta;
    public static bool Installed { get; private set; }
    public static string? Failure { get; private set; }
    public static void RecordDelta(double dt) => _lastDelta = dt;

    public static void Install(Action<double> draw)
    {
        _draw = draw;

        if (Installed)
        {
            return;
        }

        try
        {
            Failure = Patch();
            Installed = Failure is null;
        }
        catch (Exception ex)
        {
            Failure = ex.Message;
            Installed = false;
        }
    }

    public static void AfterDrawFps()
    {
        if (Program.DrawUI)
        {
            return;
        }

        _draw?.Invoke(_lastDelta);
    }

    private static string? Patch()
    {
        Type? harmonyType = FindType("HarmonyLib.Harmony");
        Type? harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");

        if (harmonyType is null || harmonyMethodType is null)
        {
            return "Harmony is not loaded";
        }

        MethodInfo? target = typeof(Program).GetMethod(
            TargetMethod, BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes);

        if (target is null)
        {
            return $"KSA has no {TargetMethod}() to hook; the game has probably moved it";
        }

        MethodInfo postfix = typeof(HiddenUiPatch).GetMethod(
            nameof(AfterDrawFps), BindingFlags.Public | BindingFlags.Static)!;

        MethodInfo? patch = harmonyType.GetMethod("Patch");

        if (patch is null)
        {
            return "Harmony.Patch is not where it used to be";
        }

        object instance = Activator.CreateInstance(harmonyType, "com.elevatorctln.ksatelemetryoverlay")!;
        object wrapped = Activator.CreateInstance(harmonyMethodType, postfix)!;

        ParameterInfo[] parameters = patch.GetParameters();
        object?[] arguments = new object?[parameters.Length];
        arguments[0] = target;

        int slot = Array.FindIndex(parameters, p => p.Name == "postfix");

        if (slot < 0)
        {
            return "Harmony.Patch takes no postfix";
        }

        arguments[slot] = wrapped;
        patch.Invoke(instance, arguments);

        return null;
    }

    private static Type? FindType(string fullName)
    {
        Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();

        for (int i = 0; i < loaded.Length; i++)
        {
            Type? found = loaded[i].GetType(fullName, throwOnError: false);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
