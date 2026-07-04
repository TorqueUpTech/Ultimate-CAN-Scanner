using Microsoft.Win32;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// Discovers installed SAE J2534 PassThru drivers from the registry. Each vendor device registers
/// a subkey under <c>SOFTWARE\PassThruSupport.04.04</c> carrying a display <c>Name</c> and the
/// <c>FunctionLibrary</c> path to its DLL.
///
/// 64-bit drivers register in the native (Registry64) view; 32-bit drivers register under
/// WOW6432Node (Registry32). This process is x64 and can only load 64-bit DLLs, so 32-bit entries
/// are still listed (so the user sees their device) but flagged as unusable.
/// </summary>
internal static class J2534Registry
{
    private const string SubKey = @"SOFTWARE\PassThruSupport.04.04";

    /// <summary>A discovered J2534 driver.</summary>
    /// <param name="Name">Vendor display name.</param>
    /// <param name="FunctionLibrary">Full path to the driver DLL to load.</param>
    /// <param name="Loadable">False for 32-bit drivers this x64 process cannot load.</param>
    public readonly record struct Driver(string Name, string FunctionLibrary, bool Loadable);

    public static IReadOnlyList<Driver> Discover()
    {
        var byPath = new Dictionary<string, Driver>(StringComparer.OrdinalIgnoreCase);

        // Native (64-bit) drivers first — these win on any path collision because they are loadable.
        foreach (Driver d in Read(RegistryView.Registry64, loadable: true))
            byPath[d.FunctionLibrary] = d;

        // WOW6432Node (32-bit) drivers: surface them, but only if we didn't already find a 64-bit
        // driver at the same path.
        foreach (Driver d in Read(RegistryView.Registry32, loadable: false))
            byPath.TryAdd(d.FunctionLibrary, d);

        return byPath.Values
            .OrderByDescending(d => d.Loadable)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<Driver> Read(RegistryView view, bool loadable)
    {
        using RegistryKey hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using RegistryKey? root = hklm.OpenSubKey(SubKey);
        if (root is null)
            yield break;

        foreach (string name in root.GetSubKeyNames())
        {
            using RegistryKey? dev = root.OpenSubKey(name);
            string? dll = dev?.GetValue("FunctionLibrary") as string;
            if (string.IsNullOrWhiteSpace(dll))
                continue;
            string display = dev!.GetValue("Name") as string ?? name;
            yield return new Driver(display, dll!, loadable);
        }
    }
}
