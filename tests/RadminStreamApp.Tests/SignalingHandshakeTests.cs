using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

/// <summary>
/// Handshake de sala com senha, ponta a ponta contra um SignalingServer de verdade.
/// Cobre o caso que fazia o modal de senha aparecer quatro vezes: o viewer manda várias
/// mensagens antes de autenticar e cada uma provoca um AUTH_REQUIRED.
/// </summary>
public class SignalingHandshakeTests : IDisposable
{
    private readonly SignalingServer _server = new();
    private readonly int _port = FreePort();

    public SignalingHandshakeTests()
    {
        _server.RoomPassword = "sala-secreta";
        _server.IsStreaming = true;
        Assert.True(_server.Start("127.0.0.1", _port), "servidor de sinalização não subiu");
    }

    public void Dispose() => _server.Stop();

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}"), CancellationToken.None);
        return ws;
    }

    private static async Task SendAsync(ClientWebSocket ws, SignalingMessage msg)
    {
        var bytes = Encoding.UTF8.GetBytes(SignalingMessage.Serialize(msg));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<SignalingMessage> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16384];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);

        var msg = SignalingMessage.Deserialize(text);
        Assert.NotNull(msg);
        return msg!;
    }

    [Fact]
    public async Task RepeatedMessagesReuseTheSameChallenge()
    {
        using var ws = await ConnectAsync();

        // O viewer real dispara CLIENT_CONNECTED e um "ice" por candidato antes de autenticar.
        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var first = await ReceiveAsync(ws);

        await SendAsync(ws, new SignalingMessage { Type = "ice", Data = "candidato-1" });
        var second = await ReceiveAsync(ws);

        await SendAsync(ws, new SignalingMessage { Type = "ice", Data = "candidato-2" });
        var third = await ReceiveAsync(ws);

        Assert.Equal("AUTH_REQUIRED", first.Type);
        Assert.Equal("AUTH_REQUIRED", second.Type);
        Assert.Equal("AUTH_REQUIRED", third.Type);
        Assert.False(string.IsNullOrEmpty(first.Data));

        // Se o desafio mudasse a cada resposta, a senha certa digitada no modal seria
        // avaliada contra um desafio já substituído e viraria "senha incorreta".
        Assert.Equal(first.Data, second.Data);
        Assert.Equal(first.Data, third.Data);
    }

    [Fact]
    public async Task CorrectPasswordIsAccepted()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var challenge = await ReceiveAsync(ws);

        var proof = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("sala-secreta"), challenge.Data!);
        await SendAsync(ws, new SignalingMessage { Type = "AUTH", Data = proof });

        Assert.Equal("AUTH_OK", (await ReceiveAsync(ws)).Type);
    }

    [Fact]
    public async Task WrongPasswordIsRejectedAndANewChallengeIsIssued()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var challenge = await ReceiveAsync(ws);

        var wrong = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("errada"), challenge.Data!);
        await SendAsync(ws, new SignalingMessage { Type = "AUTH", Data = wrong });

        var fail = await ReceiveAsync(ws);
        Assert.Equal("AUTH_FAIL", fail.Type);

        // O desafio seguinte vem junto da recusa: sem ele o viewer ficaria sem com o que
        // responder e a segunda tentativa não sairia do lugar.
        Assert.False(string.IsNullOrEmpty(fail.Data));
        Assert.NotEqual(challenge.Data, fail.Data);
    }

    [Fact]
    public async Task RetryAfterWrongPasswordSucceeds()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var challenge = await ReceiveAsync(ws);

        await SendAsync(ws, new SignalingMessage
        {
            Type = "AUTH",
            Data = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("errada"), challenge.Data!)
        });
        var fail = await ReceiveAsync(ws);

        await SendAsync(ws, new SignalingMessage
        {
            Type = "AUTH",
            Data = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("sala-secreta"), fail.Data!)
        });

        Assert.Equal("AUTH_OK", (await ReceiveAsync(ws)).Type);
    }

    [Fact]
    public async Task StatusCheckAnswersWithoutAuthentication()
    {
        using var ws = await ConnectAsync();

        // É o que mantém a bolinha de status dos amigos funcionando em salas com senha.
        await SendAsync(ws, new SignalingMessage { Type = "STATUS_CHECK" });

        var response = await ReceiveAsync(ws);
        Assert.Equal("STATUS_RESPONSE", response.Type);
        Assert.Equal("STREAMING", response.Data);
    }
}
