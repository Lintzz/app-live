using StreamLiveApp;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using Xunit;

namespace StreamLiveApp.Tests;

/// <summary>
/// O VideoCapturer entrega o quadro em BGRA cru — o formato que o DXGI já produz — e deixa a
/// conversão de cor para o swscale do FFmpeg. Antes havia uma conversão BGRA→BGR24 escrita à
/// mão na thread de captura; tirá-la rendeu ~8% no tempo por quadro a 1080p e, principalmente,
/// uma conversão a menos no caminho (PSNR contra a imagem original 45,8 dB, contra 41,2 dB).
///
/// Este teste existe para travar essa decisão: se um upgrade do SIPSorcery parar de aceitar
/// Bgra, o build acusa aqui em vez de a transmissão sair preta em campo.
/// </summary>
public class VideoEncoderFormatTests
{
    private const int Width = 320;
    private const int Height = 240;

    public VideoEncoderFormatTests() => StreamManager.EnsureMediaInitialized();

    private static byte[] Frame(int bytesPerPixel)
    {
        var frame = new byte[Width * Height * bytesPerPixel];
        for (int i = 0; i < frame.Length; i++) frame[i] = (byte)(i % 256);
        return frame;
    }

    [Fact]
    public void EncoderAcceptsTheBgraFrameTheCapturerProduces()
    {
        using var encoder = new FFmpegVideoEncoder();

        var encoded = encoder.EncodeVideo(
            Width, Height, Frame(VideoCapturer.BytesPerPixel),
            VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.H264);

        Assert.NotNull(encoded);
        Assert.NotEmpty(encoded);
    }

    [Fact]
    public void CapturerDeclaresFourBytesPerPixel()
    {
        // O preview do host dimensiona o WriteableBitmap por esta constante; se ela e o
        // formato emitido saírem de sincronia, o Marshal.Copy escreve além do back buffer.
        Assert.Equal(4, VideoCapturer.BytesPerPixel);
    }

    [Fact]
    public void EncodedBgraFrameDecodesBackToAnImageOfTheSameSize()
    {
        using var encoder = new FFmpegVideoEncoder();

        byte[]? encoded = null;
        // O primeiro quadro pode sair vazio enquanto o encoder enche o pipeline.
        for (int i = 0; i < 10 && (encoded == null || encoded.Length == 0); i++)
        {
            encoded = encoder.EncodeVideo(Width, Height, Frame(VideoCapturer.BytesPerPixel),
                VideoPixelFormatsEnum.Bgra, VideoCodecsEnum.H264);
        }

        Assert.NotNull(encoded);
        Assert.NotEmpty(encoded!);

        using var decoder = new FFmpegVideoEncoder();
        var samples = decoder.DecodeVideo(encoded!, VideoPixelFormatsEnum.Bgr, VideoCodecsEnum.H264).ToList();

        // O viewer decodifica em BGR24 — é o que o WriteableBitmap dele espera.
        Assert.NotEmpty(samples);
        Assert.Equal((uint)Width, samples[0].Width);
        Assert.Equal((uint)Height, samples[0].Height);
    }
}
