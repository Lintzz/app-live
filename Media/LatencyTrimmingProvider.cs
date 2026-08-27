using System;
using NAudio.Wave;

namespace RadminStreamApp
{
    /// <summary>
    /// Mantém a latência do áudio sob controle descartando o excedente acumulado.
    ///
    /// A causa do atraso é física: o host captura no relógio da placa de som dele e o viewer
    /// toca no relógio da dele. Uma diferença de centésimos de por cento já faz a fila crescer
    /// sem parar, e o áudio vai ficando cada vez mais atrás do vídeo ao longo da sessão.
    ///
    /// Em vez de zerar a fila inteira de tempos em tempos (o que dá um corte audível), aqui o
    /// excedente é jogado fora aos poucos, na própria leitura, sempre que a fila passa do teto.
    /// </summary>
    public sealed class LatencyTrimmingProvider : IWaveProvider
    {
        private readonly BufferedWaveProvider _source;
        private readonly int _maxBytes;
        private readonly int _targetBytes;
        private byte[] _discardBuffer = Array.Empty<byte>();

        public WaveFormat WaveFormat => _source.WaveFormat;

        /// <summary>Quanto de áudio já foi descartado por atraso — útil em diagnóstico.</summary>
        public long TrimmedBytes { get; private set; }

        public LatencyTrimmingProvider(BufferedWaveProvider source, TimeSpan maxLatency, TimeSpan targetLatency)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));

            _maxBytes = BytesFor(source.WaveFormat, maxLatency);
            _targetBytes = BytesFor(source.WaveFormat, targetLatency);
        }

        private static int BytesFor(WaveFormat format, TimeSpan duration)
        {
            var bytes = (int)(format.AverageBytesPerSecond * duration.TotalSeconds);
            return bytes - (bytes % format.BlockAlign); // sempre em amostras inteiras
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            TrimIfBehind();
            return _source.Read(buffer, offset, count);
        }

        private void TrimIfBehind()
        {
            int excess = _source.BufferedBytes - _maxBytes;
            if (excess <= 0) return;

            // Corta até o alvo, não até o teto: senão a fila voltaria a estourar no quadro
            // seguinte e o descarte viraria constante.
            int toDiscard = _source.BufferedBytes - _targetBytes;
            toDiscard -= toDiscard % _source.WaveFormat.BlockAlign;
            if (toDiscard <= 0) return;

            if (_discardBuffer.Length < toDiscard) _discardBuffer = new byte[toDiscard];

            int discarded = _source.Read(_discardBuffer, 0, toDiscard);
            TrimmedBytes += discarded;
        }
    }
}
