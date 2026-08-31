using StreamLiveApp;
using Xunit;

namespace StreamLiveApp.Tests;

/// <summary>
/// O vigia de vídeo parado é o que faz uma queda de rede aparecer na tela. Antes dele, o viewer
/// só percebia a queda quando o TCP do Windows desistia — minutos com o último quadro congelado
/// e nada escrito. A regra é pequena e cheia de estados que não podem ser atropelados.
/// </summary>
public class ConnectionHealthTests
{
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Stalled = ViewerSession.StallThreshold + TimeSpan.FromMilliseconds(500);

    [Fact]
    public void MarcaInstavelQuandoOVideoParaComALiveNoAr()
    {
        Assert.Equal(ConnectionHealth.Instavel,
            ViewerSession.DecideHealth(ConnectionHealth.AoVivo, Stalled));
    }

    [Fact]
    public void VoltaParaAoVivoAssimQueOVideoRetoma()
    {
        Assert.Equal(ConnectionHealth.AoVivo,
            ViewerSession.DecideHealth(ConnectionHealth.Instavel, Fresh));
    }

    [Fact]
    public void NaoAcusaTravamentoExatamenteNoLimite()
    {
        // Estritamente maior: no limite ainda é engasgo normal de rede, e piscar o aviso a cada
        // um deles seria mais incômodo do que o próprio engasgo.
        Assert.Equal(ConnectionHealth.AoVivo,
            ViewerSession.DecideHealth(ConnectionHealth.AoVivo, ViewerSession.StallThreshold));
    }

    [Theory]
    [InlineData(ConnectionHealth.Conectando)]
    [InlineData(ConnectionHealth.Reconectando)]
    [InlineData(ConnectionHealth.Perdida)]
    [InlineData(ConnectionHealth.Encerrada)]
    public void NaoSobrescreveEstadoQueVeioDeOutraConclusao(ConnectionHealth atual)
    {
        // "O host encerrou" e "o socket caiu" são conclusões que a falta de imagem não pode
        // desfazer: sem esta guarda, o aviso certo era trocado por "Sinal instável" um segundo
        // depois — que é justamente o tipo de coisa que fazia o app parecer bugado.
        Assert.Equal(atual, ViewerSession.DecideHealth(atual, Stalled));
        Assert.Equal(atual, ViewerSession.DecideHealth(atual, Fresh));
    }

    [Fact]
    public void ConectandoNaoViraInstavelPorNuncaTerRecebidoImagem()
    {
        // Ainda não houve quadro nenhum: o estado correto é "conectando", não "instável".
        Assert.Equal(ConnectionHealth.Conectando,
            ViewerSession.DecideHealth(ConnectionHealth.Conectando, TimeSpan.FromMinutes(1)));
    }
}
