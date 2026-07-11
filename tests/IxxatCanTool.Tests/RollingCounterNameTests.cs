using IxxatCanTool.Decoding;
using Xunit;

namespace IxxatCanTool.Tests;

/// <summary>
/// The TX-list "Roll" auto-tick uses a name heuristic. These lock down the GM counter
/// conventions it must catch and the false-positives it must avoid.
/// </summary>
public class RollingCounterNameTests
{
    [Theory]
    // Real GM rolling/alive-counter signal names.
    [InlineData("StWhlAngAliveRollCnt")]
    [InlineData("BrkPedPosAlvRolngCnt")]
    [InlineData("ACCCmndAlvRlgCnt")]
    [InlineData("SecAxlTrqReqARollCnt")]
    [InlineData("ETC_FrmCntr")]
    [InlineData("DiagFreeRunCntr")]
    [InlineData("TCAliveRC")]
    [InlineData("ABSAutnmsBrkReqARC")]
    [InlineData("CmndAxlTrqARC")]
    [InlineData("BrkSysAutBrkReqRC")]
    [InlineData("BrkPdDrvAppPrsAlRC")]
    public void Detects_counter_names(string name) =>
        Assert.True(DbcSignalInfo.IsRollingCounterName(name));

    [Theory]
    // Not counters: checksums/protection values, and the "…Src" trap that ends in "rc".
    [InlineData("DistRollCntAvgDrvnSrc")]
    [InlineData("SecAxlTrqReqProtVal")]
    [InlineData("CrsCntrlSwStatProtValue")]
    [InlineData("ABSActvProtPVal")]
    [InlineData("EngSpeed")]
    [InlineData("VehicleSpeedAvgDrvn")]
    [InlineData("DrvMdCntrlState")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_non_counter_names(string? name) =>
        Assert.False(DbcSignalInfo.IsRollingCounterName(name));

    [Fact]
    public void Is_case_insensitive() =>
        Assert.True(DbcSignalInfo.IsRollingCounterName("SOMESIGNAL_ROLLCNT"));
}
