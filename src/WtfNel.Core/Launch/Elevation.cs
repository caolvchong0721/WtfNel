using System.Security.Principal;

namespace WtfNel.Core;

/// <summary>
/// Elevation check. The official launcher runs elevated; to parent the client process under it,
/// this process must run at the same integrity level.
/// </summary>
public static class Elevation
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
