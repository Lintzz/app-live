using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

/// <summary>
/// Mover o mouse sobre uma tela parada não gera quadro novo no DXGI: a duplicação entrega o
/// quadro com LastPresentTime zerado e o capturador o descarta, porque a imagem é a mesma.
///
/// O efeito era o cursor congelar no viewer e só saltar quando algum conteúdo mudasse — as
/// "travadas do mouse". A correção é reemitir o último quadro num ritmo bem mais curto
/// enquanto o cursor anda, em vez de esperar o intervalo do ocioso.
/// </summary>
public class CursorReemitTests
{
    private static readonly TimeSpan CursorRate = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan IdleRate = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void MovingCursorDoesNotWaitForTheIdleInterval()
    {
        // 50ms depois do último quadro, com o mouse andando: tem de sair quadro. Antes só
        // sairia aos 300ms — e ainda assim sem redesenhar o cursor.
        Assert.True(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(50), cursorMoved: true));
    }

    [Fact]
    public void StillCursorKeepsTheSlowIdleCadence()
    {
        // Nada mudou: reemitir a 30/s só gastaria banda. O quadro ocioso existe para dar
        // keyframe a quem acabou de entrar, não para animar coisa nenhuma.
        Assert.False(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(50), cursorMoved: false));
        Assert.False(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(299), cursorMoved: false));
        Assert.True(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(300), cursorMoved: false));
    }

    [Fact]
    public void MovingCursorIsStillRateLimited()
    {
        // Sem piso, a captura de 16ms viraria reemissão a 60/s de um quadro quase idêntico.
        Assert.False(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(10), cursorMoved: true));
        Assert.False(VideoCapturer.ShouldReemit(TimeSpan.FromMilliseconds(32), cursorMoved: true));
        Assert.True(VideoCapturer.ShouldReemit(CursorRate, cursorMoved: true));
    }

    [Fact]
    public void CursorCadenceIsFasterThanIdleCadence()
    {
        // A relação entre os dois é o coração da correção; se alguém empatar os intervalos,
        // a travada volta sem nenhum outro sintoma.
        Assert.True(CursorRate < IdleRate);

        var between = TimeSpan.FromMilliseconds(100);
        Assert.True(VideoCapturer.ShouldReemit(between, cursorMoved: true));
        Assert.False(VideoCapturer.ShouldReemit(between, cursorMoved: false));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void NothingIsEmittedImmediatelyAfterTheLastFrame(int ms)
    {
        var since = TimeSpan.FromMilliseconds(ms);

        Assert.False(VideoCapturer.ShouldReemit(since, cursorMoved: true));
        Assert.False(VideoCapturer.ShouldReemit(since, cursorMoved: false));
    }
}
