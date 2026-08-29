using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

/// <summary>
/// O áudio sai como PCM cru (~176 KB/s por viewer) e o Send do Fleck nunca recusa nada: com um
/// viewer congestionado, cada pacote virava escrita pendente e o host crescia ~10 MB/min por
/// viewer travado, sem teto. Estes limiares são o que segura isso.
/// </summary>
public class SignalingBackpressureTests
{
    private const long BytesPorSegundoDeAudio = 44100 * 2 * 2; // 44,1 kHz, estéreo, 16 bits

    [Fact]
    public void ViewerEmDiaRecebeTudo()
    {
        Assert.False(SignalingServer.ShouldDropAudio(0));
        Assert.False(SignalingServer.ShouldDropViewer(0));
    }

    [Fact]
    public void UmEngasgoCurtoNaoDescartaNada()
    {
        // Meio segundo de fila é normal em qualquer rede doméstica.
        var pendente = BytesPorSegundoDeAudio / 2;

        Assert.False(SignalingServer.ShouldDropAudio(pendente));
        Assert.False(SignalingServer.ShouldDropViewer(pendente));
    }

    [Fact]
    public void AlgunsSegundosDeAtrasoComecamADescartarAudio()
    {
        var pendente = BytesPorSegundoDeAudio * 3;

        Assert.True(SignalingServer.ShouldDropAudio(pendente));
        // Descartar ainda é recuperável; derrubar a conexão, não. Os dois limiares existem
        // justamente para não confundir engasgo com link morto.
        Assert.False(SignalingServer.ShouldDropViewer(pendente));
    }

    [Fact]
    public void BacklogSemRecuperacaoDerrubaAConexao()
    {
        var pendente = BytesPorSegundoDeAudio * 10;

        Assert.True(SignalingServer.ShouldDropAudio(pendente));
        Assert.True(SignalingServer.ShouldDropViewer(pendente));
    }

    [Fact]
    public void OLimiteDeDescarteVemAntesDoLimiteDeDerrubar()
    {
        // Invertê-los fecharia a conexão de quem só precisava de um instante para respirar.
        long ondeComecaODescarte = 0;
        for (long b = 0; b <= 5_000_000; b += 1_000)
        {
            if (SignalingServer.ShouldDropAudio(b)) { ondeComecaODescarte = b; break; }
        }

        Assert.True(ondeComecaODescarte > 0);
        Assert.False(SignalingServer.ShouldDropViewer(ondeComecaODescarte));
    }
}
