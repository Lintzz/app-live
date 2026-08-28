using RadminStreamApp;
using Xunit;
using Action = RadminStreamApp.VideoCapturer.DuplicationAction;

namespace RadminStreamApp.Tests;

/// <summary>
/// A transmissão congelava no último quadro e não voltava mais.
///
/// A duplicação do DXGI morre em situações comuns — jogo entrando em tela cheia exclusiva,
/// troca de desktop pelo UAC, mudança de modo de vídeo, driver reiniciando — e não volta
/// sozinha. Mas a perda chegava como o mesmo <c>false</c> de um timeout de tela parada, e a
/// recuperação estava condicionada a <c>!hasRealFrame</c>. Ou seja: depois do primeiro quadro
/// real, uma duplicação perdida nunca mais era recriada.
///
/// Medido ponta a ponta antes da correção: o viewer recebia quadros, mas o conteúdo parava de
/// mudar e só o cursor continuava andando.
/// </summary>
public class DuplicationRecoveryTests
{
    [Fact]
    public void NewFrameIsUsed()
    {
        Assert.Equal(Action.Use,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Frame, 0, hasRealFrame: true));
    }

    /// <summary>O coração da correção: perder a duplicação sempre manda recriar.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LostDuplicationIsAlwaysRecreated(bool hasRealFrame)
    {
        Assert.Equal(Action.Recreate,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Lost, 0, hasRealFrame));
    }

    [Fact]
    public void LostDuplicationIsRecreatedEvenAfterAHealthySession()
    {
        // Este é exatamente o caso que travava: a live rodou bem (hasRealFrame), o jogo entrou
        // em tela cheia, a duplicação morreu — e a captura ficava reemitindo o último quadro.
        Assert.Equal(Action.Recreate,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Lost, consecutiveTimeouts: 500, hasRealFrame: true));
    }

    [Fact]
    public void TimeoutWithAWorkingDuplicationJustRetries()
    {
        // Tela parada é o estado normal de quem está lendo algo. Não pode virar desistência.
        Assert.Equal(Action.Retry,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Timeout, 1, hasRealFrame: true));
        Assert.Equal(Action.Retry,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Timeout, 10_000, hasRealFrame: true));
    }

    [Fact]
    public void ManyTimeoutsBeforeTheFirstFrameFallBackToGdi()
    {
        // Duplicação que existe mas nunca entrega: acontece em RDP e GPU híbrida.
        Assert.Equal(Action.FallBackToGdi,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Timeout, 60, hasRealFrame: false));
    }

    [Fact]
    public void AFewTimeoutsBeforeTheFirstFrameStillRetry()
    {
        // A duplicação leva alguns tiques para engatar; desistir cedo jogaria fora o caminho
        // rápido em máquinas onde ele funciona.
        Assert.Equal(Action.Retry,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Timeout, 1, hasRealFrame: false));
        Assert.Equal(Action.Retry,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Timeout, 59, hasRealFrame: false));
    }

    /// <summary>
    /// Uma perda nunca pode ser confundida com "esta máquina não suporta DXGI": cair para o
    /// GDI de vez custaria desempenho pelo resto da transmissão sem necessidade.
    /// </summary>
    [Fact]
    public void LostIsNeverConfusedWithAnUnsupportedDuplication()
    {
        Assert.NotEqual(Action.FallBackToGdi,
            VideoCapturer.DecideDuplicationAction(DuplicationFrame.Lost, 60, hasRealFrame: false));
    }
}
