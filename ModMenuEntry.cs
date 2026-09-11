namespace KSATelemetryOverlay;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ModMenuEntryAttribute : Attribute
{
    public string MenuName { get; }
    public string? IsModMenuActivePropertyName { get; }

    public ModMenuEntryAttribute(string menuName, string? isModMenuActivePropertyName = null)
    {
        MenuName = menuName;
        IsModMenuActivePropertyName = isModMenuActivePropertyName;
    }
}
