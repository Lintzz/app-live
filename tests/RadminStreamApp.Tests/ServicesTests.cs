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
    public async Task UnreachableHostIsOffline()
    {
        // 203.0.113.0/24 é reservado para documentação: nunca responde.
        var status = await FriendStatusService.CheckAsync("203.0.113.1");

        Assert.False(status.IsOnline);
        Assert.False(status.IsStreaming);
    }

    [Fact]
    public async Task ClosedPortOnLoopbackIsOffline()
    {
        var status = await FriendStatusService.CheckAsync("127.0.0.1", port: 9);

        Assert.False(status.IsOnline);
    }
}

public class LatencyTrimmingProviderTests
{
    private static NAudio.Wave.BufferedWaveProvider NewSource()
        => new(new NAudio.Wave.WaveFormat(48000, 16, 1)) { BufferDuration = System.TimeSpan.FromSeconds(5) };

    [Fact]
    public void PassesAudioThroughWhenLatencyIsLow()
    {
        var source = NewSource();
        var trimmer = new LatencyTrimmingProvider(source,
            System.TimeSpan.FromMilliseconds(250), System.TimeSpan.FromMilliseconds(80));

        source.AddSamples(new byte[960], 0, 960); // 10 ms
        var buffer = new byte[960];

        Assert.Equal(960, trimmer.Read(buffer, 0, 960));
        Assert.Equal(0, trimmer.TrimmedBytes);
    }

    [Fact]
    public void DiscardsBacklogAboveTheCeiling()
    {
        var source = NewSource();
        var trimmer = new LatencyTrimmingProvider(source,
            System.TimeSpan.FromMilliseconds(250), System.TimeSpan.FromMilliseconds(80));

        // 1 segundo enfileirado: muito acima do teto de 250 ms.
        source.AddSamples(new byte[96000], 0, 96000);
        trimmer.Read(new byte[960], 0, 960);

        Assert.True(trimmer.TrimmedBytes > 0);
        // Sobra em torno do alvo (80 ms = 7680 bytes), menos o que acabou de ser lido.
        Assert.True(source.BufferedDuration < System.TimeSpan.FromMilliseconds(120),
            $"restou {source.BufferedDuration.TotalMilliseconds}ms");
    }

    [Fact]
    public void KeepsLatencyBoundedAcrossManyCycles()
    {
        var source = NewSource();
        var trimmer = new LatencyTrimmingProvider(source,
            System.TimeSpan.FromMilliseconds(250), System.TimeSpan.FromMilliseconds(80));
        var buffer = new byte[960];

        // Host produz 10% mais rápido do que o viewer consome: é o drift de relógio que
        // fazia o áudio atrasar sem parar.
        for (int i = 0; i < 500; i++)
        {
            source.AddSamples(new byte[1056], 0, 1056);
            trimmer.Read(buffer, 0, buffer.Length);
        }

        Assert.True(source.BufferedDuration <= System.TimeSpan.FromMilliseconds(250),
            $"latência escapou para {source.BufferedDuration.TotalMilliseconds}ms");
    }
}
