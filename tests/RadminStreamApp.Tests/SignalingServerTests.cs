using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

public class NormalizeIpTests
{
    [Theory]
    [InlineData("::1", "127.0.0.1")]
    [InlineData("::ffff:26.10.0.5", "26.10.0.5")]
    [InlineData("::FFFF:192.168.0.1", "192.168.0.1")]
    [InlineData("26.10.0.5", "26.10.0.5")]
    [InlineData("  26.10.0.5  ", "26.10.0.5")]
    public void NormalizesToPlainIpv4(string input, string expected)
    {
        Assert.Equal(expected, SignalingServer.NormalizeIp(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyInputBecomesEmptyString(string? input)
    {
        Assert.Equal(string.Empty, SignalingServer.NormalizeIp(input));
    }

    [Fact]
    public void PreservesRealIpv6()
    {
        // Só o formato IPv4-mapeado deve ser reescrito; um IPv6 de verdade passa intacto.
        Assert.Equal("fe80::1234", SignalingServer.NormalizeIp("fe80::1234"));
    }
}
