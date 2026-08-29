namespace RadminStreamApp
{
    /// <summary>
    /// Como está a conexão de uma live que você assiste. Antes disso o estado da sessão vivia
    /// espalhado em <c>StatusText</c>, que recebia de tudo — texto de progresso do SDP, o enum
    /// cru do SIPSorcery e mensagens de erro do decoder —, e por isso não dava para a UI
    /// reagir a nada. Aqui cada valor tem uma decisão associada.
    /// </summary>
    public enum ConnectionHealth
    {
        /// <summary>Handshake em andamento; ainda não houve imagem.</summary>
        Conectando,

        /// <summary>Vídeo chegando normalmente.</summary>
        AoVivo,

        /// <summary>Socket vivo, mas o vídeo parou de chegar. Costuma se resolver sozinho.</summary>
        Instavel,

        /// <summary>A conexão caiu e o cliente está tentando voltar.</summary>
        Reconectando,

        /// <summary>Esgotaram as tentativas automáticas. Resta o botão de reconectar.</summary>
        Perdida,

        /// <summary>O host encerrou a live de propósito — não é erro, e não deve virar retry.</summary>
        Encerrada
    }
}
