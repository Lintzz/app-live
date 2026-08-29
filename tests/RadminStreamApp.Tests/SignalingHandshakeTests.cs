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

    private const string Password = "sala-secreta";

    public SignalingHandshakeTests()
    {
        _server.RoomPassword = Password;
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

    private static Task AuthenticateAsync(ClientWebSocket ws, string password, string challenge)
        => SendAsync(ws, new SignalingMessage
        {
            Type = "AUTH",
            Data = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey(password), challenge)
        });

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

        await AuthenticateAsync(ws, Password, challenge.Data!);

        Assert.Equal("AUTH_OK", (await ReceiveAsync(ws)).Type);
    }

    [Fact]
    public async Task WrongPasswordIsRejectedAndANewChallengeIsIssued()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var challenge = await ReceiveAsync(ws);

        await AuthenticateAsync(ws, "errada", challenge.Data!);

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

        await AuthenticateAsync(ws, "errada", challenge.Data!);
        var fail = await ReceiveAsync(ws);

        await AuthenticateAsync(ws, Password, fail.Data!);

        Assert.Equal("AUTH_OK", (await ReceiveAsync(ws)).Type);
    }

    [Fact]
    public async Task ReplayingAnOldProofIsRejected()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var challenge = await ReceiveAsync(ws);

        var proof = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("errada"), challenge.Data!);
        await SendAsync(ws, new SignalingMessage { Type = "AUTH", Data = proof });
        await ReceiveAsync(ws); // AUTH_FAIL — o desafio queima aqui

        // Mesma prova, desafio já substituído: não pode passar.
        await SendAsync(ws, new SignalingMessage { Type = "AUTH", Data = proof });
        Assert.Equal("AUTH_FAIL", (await ReceiveAsync(ws)).Type);
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

    [Fact]
    public async Task PingAnswersWithoutAuthenticationAndEchoesTheData()
    {
        using var ws = await ConnectAsync();

        // O PONG carrega de volta o tick enviado; é dele que sai a latência mostrada na UI.
        await SendAsync(ws, new SignalingMessage { Type = "PING", Data = "12345" });

        var pong = await ReceiveAsync(ws);
        Assert.Equal("PONG", pong.Type);
        Assert.Equal("12345", pong.Data);
    }

    [Fact]
    public async Task UnauthenticatedClientIsNotCountedAsViewer()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        await ReceiveAsync(ws); // AUTH_REQUIRED

        // Sem autenticar, não conta e não recebe difusão — senão o áudio binário sairia
        // para quem ainda não provou saber a senha.
        Assert.Equal(0, _server.ConnectedClientsCount);
        Assert.False(_server.HasBroadcastTargets);
    }

    [Fact]
    public async Task AuthenticatedViewerBecomesABroadcastTarget()
    {
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var challenge = await ReceiveAsync(ws);
        await AuthenticateAsync(ws, Password, challenge.Data!);
        await ReceiveAsync(ws); // AUTH_OK

        // Depois de autenticar, o viewer precisa se anunciar de novo — agora criptografado.
        var key = CryptoHelper.DeriveKey(Password);
        var hello = SignalingMessage.Serialize(new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var bytes = Encoding.UTF8.GetBytes(CryptoHelper.EncryptText(hello, key));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        await WaitUntilAsync(() => _server.ConnectedClientsCount == 1);

        Assert.Equal(1, _server.ConnectedClientsCount);
        Assert.True(_server.HasBroadcastTargets);
    }

    [Fact]
    public async Task EncryptedAuthIsAcceptedAfterAReconnect()
    {
        // Cenário real: a rede do viewer caiu, ele voltou num socket novo e ainda carrega a
        // chave da sessão anterior, então o próprio AUTH sai criptografado. O host decidia
        // mandar AUTH_REQUIRED antes de tentar descriptografar, não reconhecia aquele AUTH,
        // e os dois ficavam num ping-pong AUTH_REQUIRED ↔ AUTH na velocidade do RTT — a live
        // simplesmente nunca voltava em sala com senha.
        using var ws = await ConnectAsync();

        await SendAsync(ws, new SignalingMessage { Type = "CLIENT_CONNECTED" });
        var challenge = await ReceiveAsync(ws);

        var key = CryptoHelper.DeriveKey(Password);
        var auth = SignalingMessage.Serialize(new SignalingMessage
        {
            Type = "AUTH",
            Data = CryptoHelper.ComputeAuthProof(key, challenge.Data!)
        });
        var bytes = Encoding.UTF8.GetBytes(CryptoHelper.EncryptText(auth, key));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.Equal("AUTH_OK", (await ReceiveAsync(ws)).Type);
    }

    [Fact]
    public async Task EncryptedStatusCheckIsAnsweredOnce()
    {
        // O STATUS_CHECK volta a passar pelo caminho normal depois da mudança de ordem: ele
        // chega cifrado quando a sessão já está estabelecida, e continua sendo respondido.
        using var ws = await ConnectAsync();

        var key = CryptoHelper.DeriveKey(Password);
        var check = SignalingMessage.Serialize(new SignalingMessage { Type = "STATUS_CHECK" });
        var bytes = Encoding.UTF8.GetBytes(CryptoHelper.EncryptText(check, key));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);

        var response = await ReceiveAsync(ws);
        Assert.Equal("STATUS_RESPONSE", response.Type);
        Assert.Equal("STREAMING", response.Data);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
    }
}
