using IxxatCanTool.Can;
using Xunit;

namespace IxxatCanTool.Tests;

public class CanIdFilterTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("zzz")]        // no valid tokens
    public void Parse_returns_null_when_nothing_usable(string? text)
    {
        Assert.Null(CanIdFilter.Parse(text, exclude: false));
    }

    [Fact]
    public void Allowlist_passes_only_listed_ids()
    {
        var f = CanIdFilter.Parse("100 7E8", exclude: false)!;
        Assert.True(f.Passes(0x100));
        Assert.True(f.Passes(0x7E8));
        Assert.False(f.Passes(0x101));
    }

    [Fact]
    public void Blocklist_passes_everything_except_listed_ids()
    {
        var f = CanIdFilter.Parse("100 7E8", exclude: true)!;
        Assert.False(f.Passes(0x100));
        Assert.False(f.Passes(0x7E8));
        Assert.True(f.Passes(0x101));
    }

    [Fact]
    public void Range_is_inclusive_on_both_ends()
    {
        var f = CanIdFilter.Parse("700-7FF", exclude: false)!;
        Assert.True(f.Passes(0x700));
        Assert.True(f.Passes(0x7FF));
        Assert.True(f.Passes(0x750));
        Assert.False(f.Passes(0x6FF));
        Assert.False(f.Passes(0x800));
    }

    [Fact]
    public void Reversed_range_is_normalised()
    {
        var f = CanIdFilter.Parse("7FF-700", exclude: false)!;
        Assert.True(f.Passes(0x750));
    }

    [Theory]
    [InlineData("0x100")]
    [InlineData("100")]
    [InlineData(" 100 ")]
    public void Hex_prefix_and_whitespace_are_tolerated(string text)
    {
        var f = CanIdFilter.Parse(text, exclude: false)!;
        Assert.True(f.Passes(0x100));
    }

    [Theory]
    [InlineData("100,200;300")]
    [InlineData("100  200\t300")]
    public void Mixed_separators_split_into_ids(string text)
    {
        var f = CanIdFilter.Parse(text, exclude: false)!;
        Assert.True(f.Passes(0x100));
        Assert.True(f.Passes(0x200));
        Assert.True(f.Passes(0x300));
        Assert.False(f.Passes(0x400));
    }

    [Fact]
    public void Malformed_tokens_are_skipped_but_valid_ones_kept()
    {
        var f = CanIdFilter.Parse("100 nope 200", exclude: false)!;
        Assert.Equal(2, f.Count);
        Assert.True(f.Passes(0x100));
        Assert.True(f.Passes(0x200));
    }

    [Fact]
    public void Ids_are_masked_to_29_bits()
    {
        // A driver flag bit above bit 28 must not change which arbitration ID matches.
        var f = CanIdFilter.Parse("1FFFFFFF", exclude: false)!;
        Assert.True(f.Passes(0x1FFFFFFF));
    }
}
