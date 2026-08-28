using RadminStreamApp.Services;
using Xunit;

namespace RadminStreamApp.Tests;

public class AudioExclusionServiceTests
{
    [Fact]
    public void ListOptions_AlwaysOffersNone()
    {
        var options = AudioExclusionService.ListOptions(null);

        Assert.NotEmpty(options);
        Assert.Equal(string.Empty, options[0].Name);
        Assert.Equal(AudioExclusionService.NoneDisplayName, options[0].DisplayName);
    }

    [Fact]
    public void ListOptions_KeepsCurrentSelectionEvenWhenNotRunning()
    {
        var options = AudioExclusionService.ListOptions("ProgramaQueNaoExiste_XYZ");

        var match = Assert.Single(options, o => o.Name == "ProgramaQueNaoExiste_XYZ");
        Assert.Contains("não está em execução", match.DisplayName);
    }

    [Fact]
    public void ListOptions_NeverRepeatsAProcessName()
    {
        // A escolha atual é acrescentada quando não está na lista de janelas. Se essa
        // checagem falhasse, o programa escolhido apareceria duas vezes no dropdown.
        var self = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

        var options = AudioExclusionService.ListOptions(self);
        var names = options.Select(o => o.Name).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// O dropdown só lista programas com janela — são os que o usuário reconhece. Mas o
    /// ResolvePid aceita qualquer processo, senão a escolha salva deixaria de valer sempre
    /// que o programa estivesse rodando sem janela visível.
    /// </summary>
    [Fact]
    public void ResolvePid_FindsAProcessEvenWithoutAWindow()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.Equal(IntPtr.Zero, self.MainWindowHandle);
        Assert.NotEqual(0u, AudioExclusionService.ResolvePid(self.ProcessName));
    }

    [Fact]
    public void ResolvePid_ReturnsZeroForNoSelection()
    {
        Assert.Equal(0u, AudioExclusionService.ResolvePid(null));
        Assert.Equal(0u, AudioExclusionService.ResolvePid(string.Empty));
    }

    [Fact]
    public void ResolvePid_ReturnsZeroWhenProcessIsNotRunning()
    {
        // É este 0 que faz o app cair para "capturar tudo" — e a UI avisa o usuário.
        Assert.Equal(0u, AudioExclusionService.ResolvePid("ProgramaQueNaoExiste_XYZ"));
    }

    [Fact]
    public void ResolvePid_FindsARunningProcess()
    {
        var self = System.Diagnostics.Process.GetCurrentProcess();

        Assert.NotEqual(0u, AudioExclusionService.ResolvePid(self.ProcessName));
    }
}

public class FriendStatusServiceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task BlankIpIsOffline(string? ip)
    {
        Assert.Equal(FriendStatus.Offline, await FriendStatusService.CheckAsync(ip!));
    }

    [Fact]
    public async Task NothingListeningIsOffline()
    {
        // Porta livre: ninguém atende, então não pode reportar online.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        Assert.Equal(FriendStatus.Offline, await FriendStatusService.CheckAsync("127.0.0.1", port));
    }

    [Fact]
    public async Task ReportsOnlineAndIdleForAServerThatIsNotStreaming()
    {
        var server = new RadminStreamApp.SignalingServer { IsStreaming = false };
        int port = FreePort();
        Assert.True(server.Start("127.0.0.1", port));

        try
        {
            var status = await FriendStatusService.CheckAsync("127.0.0.1", port);

            Assert.True(status.IsOnline);
            Assert.False(status.IsStreaming);
        }
        finally
        {
            server.Stop();
        }
    }

    [Fact]
    public async Task ReportsStreamingForAServerThatIsLive()
    {
        var server = new RadminStreamApp.SignalingServer { IsStreaming = true };
        int port = FreePort();
        Assert.True(server.Start("127.0.0.1", port));

        try
        {
            var status = await FriendStatusService.CheckAsync("127.0.0.1", port);

            Assert.True(status.IsOnline);
            Assert.True(status.IsStreaming);
        }
        finally
        {
            server.Stop();
        }
    }

    /// <summary>
    /// Sala com senha continua respondendo o status: é o que mantém a bolinha do amigo
    /// verde sem que você precise saber a senha dele.
    /// </summary>
    [Fact]
    public async Task PasswordProtectedRoomStillReportsItsStatus()
    {
        var server = new RadminStreamApp.SignalingServer { IsStreaming = true, RoomPassword = "segredo" };
        int port = FreePort();
        Assert.True(server.Start("127.0.0.1", port));

        try
        {
            var status = await FriendStatusService.CheckAsync("127.0.0.1", port);

            Assert.True(status.IsOnline);
            Assert.True(status.IsStreaming);
        }
        finally
        {
            server.Stop();
        }
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

public class AppSettingsTests
{
    /// <summary>
    /// Os padrões de fábrica valem na primeira execução, quando não há settings.json. O do
    /// Discord é o que impede a mesa inteira de se escutar sem ninguém entender por quê.
    /// </summary>
    [Fact]
    public void FactoryDefaultsAreTheSafeOnes()
    {
        var settings = new AppSettings();

        Assert.Equal("Discord", settings.ExcludedAudioProcessName);
        Assert.Equal(AppSettings.DefaultExcludedAudioProcessName, settings.ExcludedAudioProcessName);
        Assert.True(settings.RestrictToFriends);
        Assert.True(settings.LightweightMode);
        Assert.False(settings.ForceGdiCapture);
    }

    [Fact]
    public void SettingsSurviveARoundTripThroughJson()
    {
        var original = new AppSettings
        {
            ExcludedAudioProcessName = "Spotify",
            LightweightMode = false,
            RestrictToFriends = false,
            ForceGdiCapture = true
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(back);
        Assert.Equal(original.ExcludedAudioProcessName, back!.ExcludedAudioProcessName);
        Assert.Equal(original.LightweightMode, back.LightweightMode);
        Assert.Equal(original.RestrictToFriends, back.RestrictToFriends);
        Assert.Equal(original.ForceGdiCapture, back.ForceGdiCapture);
    }

    /// <summary>
    /// Um settings.json gravado por uma versão anterior não tem os campos novos. Eles têm de
    /// cair no padrão de fábrica, não em false — senão atualizar o app desligaria calado a
    /// restrição a amigos.
    /// </summary>
    [Fact]
    public void OlderSettingsFileKeepsTheSafeDefaultsForNewFields()
    {
        var json = """{"ExcludedAudioProcessName":"Discord"}""";

        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);

        Assert.NotNull(settings);
        Assert.True(settings!.RestrictToFriends);
        Assert.True(settings.LightweightMode);
        Assert.False(settings.ForceGdiCapture);
    }
}
