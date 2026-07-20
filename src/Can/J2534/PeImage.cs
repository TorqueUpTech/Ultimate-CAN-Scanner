using System.IO;

namespace IxxatCanTool.Can.J2534;

/// <summary>Machine architecture of a Windows PE image (DLL/EXE), from its COFF header.</summary>
internal enum PeArch { Unknown, X86, X64 }

/// <summary>
/// Reads a PE image's machine type so a J2534 driver DLL can be routed correctly: an x64 driver
/// loads in-process, an x86 driver goes through the 32-bit bridge host. Authoritative (reads the
/// COFF header) rather than trusting the registry view a driver happened to register under.
/// </summary>
internal static class PeImage
{
    private const ushort MachineI386 = 0x014c;
    private const ushort MachineAmd64 = 0x8664;

    public static PeArch ArchOf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);

            fs.Seek(0x3C, SeekOrigin.Begin);          // e_lfanew: offset of the PE header
            int peOffset = br.ReadInt32();
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x0000_4550)        // "PE\0\0" signature
                return PeArch.Unknown;

            return br.ReadUInt16() switch              // COFF Machine field
            {
                MachineAmd64 => PeArch.X64,
                MachineI386 => PeArch.X86,
                _ => PeArch.Unknown
            };
        }
        catch
        {
            return PeArch.Unknown;
        }
    }
}
