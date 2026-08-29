using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

/// <summary>
/// A espera entre tentativas era fixa em 5s — e vinha <em>antes</em> da primeira tentativa, o que
/// castigava justamente o caso comum na VPN: a queda de menos de um segundo.
/// </summary>
public class ReconnectBackoffTests
{
    [Fact]
    public void PrimeiraTentativaEImediata()
    {
        Assert.Equal(TimeSpan.Zero, SignalingClient.ComputeBackoffBase(1));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(4, 4)]
    [InlineData(5, 8)]
    public void CresceEmDobroAteOTeto(int tentativa, int segundosEsperados)
    {
        Assert.Equal(TimeSpan.FromSeconds(segundosEsperados), SignalingClient.ComputeBackoffBase(tentativa));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(50)]
    public void NuncaPassaDoTeto(int tentativa)
    {
        Assert.Equal(TimeSpan.FromSeconds(SignalingClient.MaxBackoffSeconds),
            SignalingClient.ComputeBackoffBase(tentativa));
    }

    [Fact]
    public void AJanelaTotalDeTentativasCabeEmMenosDeUmMinuto()
    {
        // O botão "Reconectar" só aparece quando isto acaba. Se a janela crescer demais, o
        // usuário fica olhando para uma célula morta sem saber que pode agir.
        var total = TimeSpan.Zero;
        for (int i = 1; i <= SignalingClient.MaxReconnectAttempts; i++)
            total += SignalingClient.ComputeBackoffBase(i);

        Assert.InRange(total, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void OJitterNaoAtrasaUmaTentativaImediata()
    {
        Assert.Equal(TimeSpan.Zero, SignalingClient.ApplyJitter(TimeSpan.Zero, 0.99));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void OJitterFicaDentroDaFaixaCombinada(double roll)
    {
        var baseDelay = TimeSpan.FromSeconds(8);
        var jittered = SignalingClient.ApplyJitter(baseDelay, roll);

        Assert.InRange(jittered,
            baseDelay * (1 - SignalingClient.JitterFraction),
            baseDelay * (1 + SignalingClient.JitterFraction));
    }

    [Fact]
    public void OJitterEspalhaParaOsDoisLados()
    {
        var baseDelay = TimeSpan.FromSeconds(10);

        Assert.True(SignalingClient.ApplyJitter(baseDelay, 0.0) < baseDelay);
        Assert.True(SignalingClient.ApplyJitter(baseDelay, 1.0) > baseDelay);
        Assert.Equal(baseDelay, SignalingClient.ApplyJitter(baseDelay, 0.5));
    }
}
