using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

public class SignalingMessageTests
{
    [Fact]
    public void RoundTripsAllFields()
    {
        var json = SignalingMessage.Serialize(new SignalingMessage
        {
            Type = "offer",
            Data = "{\"sdp\":\"v=0\"}",
            SenderId = "client-1"
        });

        var parsed = SignalingMessage.Deserialize(json);

        Assert.NotNull(parsed);
        Assert.Equal("offer", parsed!.Type);
        Assert.Equal("{\"sdp\":\"v=0\"}", parsed.Data);
        Assert.Equal("client-1", parsed.SenderId);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{unbalanced")]
    public void DeserializeReturnsNullOnGarbage(string input)
    {
        // O servidor recebe qualquer coisa da rede: parse inválido não pode lançar.
        Assert.Null(SignalingMessage.Deserialize(input));
    }

    [Fact]
    public void DeserializeToleratesMissingFields()
    {
        var parsed = SignalingMessage.Deserialize("{\"Type\":\"PING\"}");

        Assert.NotNull(parsed);
        Assert.Equal("PING", parsed!.Type);
        Assert.Null(parsed.Data);
    }
}
