using Microsoft.Win32;

namespace IxxatCanTool.Can.J2534;

/// <summary>
/// Discovers installed SAE J2534 PassThru drivers from the registry. Each vendor device registers
/// a subkey under <c>SOFTWARE\PassThruSupport.04.04</c> carrying a display <c>Name</c> and the
/// <c>FunctionLibrary</c> path to its DLL.
///
/// 64-bit drivers register in the native (Registry64) view; 32-bit drivers under WOW6432Node
/// (Registry32). Both are surfaced: an x64 driver loads in-process, an x86 driver is reached via
/// the 32-bit bridge host — so the actual DLL bitness (read from the PE header at connect time),
/// not the registry view, decides how it opens.
/// </summary>
internal static class J2534Registry
{
    private const string SubKey = @"SOFTWARE\PassThruSupport.04.04";

    /// <summary>A discovered J2534 driver.</summary>
    /// <param name="Name">Vendor display name.</param>
    /// <param name="FunctionLibrary">Full path to the driver DLL to load.</param>
    public readonly record struct Driver(string Name, string FunctionLibrary);

    public static IReadOnlyList<Driver> Discover()
    {
        var byPath = new Dictionary<string, Driver>(StringComparer.OrdinalIgnoreCase);

        // Read both registry views; a DLL registered in both is listed once (path is the key).
        foreach (Driver d in Read(RegistryView.Registry64))
            byPath[d.FunctionLibrary] = d;
        foreach (Driver d in Read(RegistryView.Registry32))
            byPath.TryAdd(d.FunctionLibrary, d);

        return byPath.Values
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<Driver> Read(RegistryView view)
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
            yield return new Driver(display, dll!);
        }
    }
}
