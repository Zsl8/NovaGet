namespace NovaGet.Core.Tests.Infrastructure;

/// <summary>A fact that only runs on Windows (DPAPI, registry, shell APIs).</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Requires Windows.";
        }
    }
}
