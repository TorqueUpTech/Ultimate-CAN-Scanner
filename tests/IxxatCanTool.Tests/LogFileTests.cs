using System;
using System.IO;
using IxxatCanTool.Can;
using IxxatCanTool.Logging;
using Xunit;

namespace IxxatCanTool.Tests;

/// <summary>
/// Covers the three trace formats LogFile auto-detects — CAN-Tool CSV, the quoted IXXAT
/// canAnalyser3 export (double-quoted fields + comma-in-quotes "No" counter), and headerless
/// legacy GM — since these are exactly the parsing edge cases fixed this session.
/// </summary>
public class LogFileTests
{
    private static string Temp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "can-tool-test-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Reads_can_tool_format()
    {
        string path = Temp(
            "Time(s),Dir,ID,Type,DLC,Data\n" +
            "39.513052,Rx,0C7,DATA,4,03FE0000\n" +
            "1.0,Tx,18FEF100x,DATA,3,AABBCC\n");
        try
        {
            var f = LogFile.Read(path);
            Assert.Equal(2, f.Count);
            Assert.Equal(0x0C7u, f[0].Identifier);
            Assert.False(f[0].IsExtended);
            Assert.Equal(CanDirection.Rx, f[0].Direction);
            Assert.Equal("03FE0000", Convert.ToHexString(f[0].Data));
            Assert.Equal(0x18FEF100u, f[1].Identifier);
            Assert.True(f[1].IsExtended);
            Assert.Equal(CanDirection.Tx, f[1].Direction);
            Assert.Equal("AABBCC", Convert.ToHexString(f[1].Data));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reads_cananalyser3_quoted_export()
    {
        // The "No" field carries a thousands-separator comma inside quotes — the trap that used
        // to break the naive comma split and leave Play greyed out.
        string path = Temp(
            "\"Bus\",\"No\",\"Time (abs)\",\"State\",\"ID (hex)\",\"DLC\",\"Data (hex)\",\"ASCII\"\r\n" +
            "\"USB-to-CAN V2 compact  CAN-1\",\"174,213\",\"00:01:21.736\",\"      \",\"1ED\",\"8\",\"C1 90 05 0D 05 38 75 6D\",\".....8um\"\r\n");
        try
        {
            var f = LogFile.Read(path);
            Assert.Single(f);
            Assert.Equal(0x1EDu, f[0].Identifier);
            Assert.False(f[0].IsExtended);
            Assert.Equal(CanDirection.Rx, f[0].Direction);
            Assert.Equal("C190050D0538756D", Convert.ToHexString(f[0].Data));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Reads_legacy_gm_headerless()
    {
        string path = Temp("idx,cnt,00:00:01,st,0C7,4,03 FE 00 00\n");
        try
        {
            var f = LogFile.Read(path);
            Assert.Single(f);
            Assert.Equal(0x0C7u, f[0].Identifier);
            Assert.Equal("03FE0000", Convert.ToHexString(f[0].Data));
        }
        finally { File.Delete(path); }
    }
}
