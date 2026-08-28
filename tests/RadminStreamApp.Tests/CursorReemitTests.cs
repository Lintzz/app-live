using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

/// <summary>
/// O DXGI só entrega quadro quando a imagem muda. Com a tela parada — e mover o mouse não
/// conta como mudança para ele — o capturador reemite o último quadro, e o ritmo dessa
/// reemissão é o piso de fps da transmissão inteira.
///
/// Esse piso já foi 300ms, para poupar CPU. O efeito em campo era ruim nos dois sentidos: o
/// cursor congelava sobre tela parada, e a transmissão caía para ~9 fps e parecia travada.
/// Medido a 1080p com a tela parada: 300ms davam 8,7 fps a 5,5% de um núcleo; a cadência da
/// captura dá 33,3 fps a 17,6%. O caminho GDI, que nunca teve freio, dava 30,8 fps a 42,7%.
/// </summary>
public class CursorReemitTests
{
    private static readonly TimeSpan CaptureCadence = TimeSpan.FromMilliseconds(16);

    [Fact]
    public void ReemitsAtTheCaptureCadence()
    {
        Assert.True(VideoCapturer.ShouldReemit(CaptureCadence));
        Assert.True(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(20)));
        Assert.True(VideoCapturer.ShouldReemit(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void DoesNotReemitTwiceWithinTheSameTick()
    {
        // Sem um piso, uma passagem mais rápida da captura reemitiria o mesmo quadro duas
        // vezes seguidas, gastando encode para entregar imagem idêntica.
        Assert.False(VideoCapturer.ShouldReemit(TimeSpan.Zero));
        Assert.False(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(15)));
    }

    /// <summary>
    /// O piso tem de ficar bem abaixo da percepção de travamento. Este teste falha se alguém
    /// voltar a subir o intervalo para economizar CPU — foi exatamente o que fez a
    /// transmissão parecer congelada com a tela parada.
    /// </summary>
    [Fact]
    public void TheFloorStaysFastEnoughToLookLive()
    {
        // 50ms = 20 fps já é o limite do aceitável; nada acima disso pode passar.
        Assert.True(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(50)),
            "o piso de reemissão subiu: a transmissão volta a parecer travada com a tela parada");
    }

    [Fact]
    public void ACursorOnlyMoveStillProducesAFrame()
    {
        // Mover o mouse não gera quadro novo no DXGI. Como a reemissão agora acontece a cada
        // cadência de captura, o movimento do cursor sai junto sem precisar de caso especial.
        Assert.True(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(33)));
    }
}
