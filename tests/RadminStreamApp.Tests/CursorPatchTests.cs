using System.Drawing;
using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

/// <summary>
/// O cursor é desenhado direto no buffer do DXGI, que só é reescrito quando a imagem muda.
/// Para as reemissões não empilharem cursores, os pixels sob ele são guardados antes e
/// devolvidos depois. É aritmética de stride: errar aqui não quebra o build, corrompe a
/// imagem que o amigo vê.
/// </summary>
public class CursorPatchTests
{
    private const int Bpp = 4;

    private static byte[] Canvas(int width, int height)
    {
        var buffer = new byte[width * height * Bpp];
        for (int i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i % 251);
        return buffer;
    }

    [Fact]
    public void SaveThenRestoreLeavesTheBufferByteForByteIdentical()
    {
        const int w = 64, h = 48;
        var buffer = Canvas(w, h);
        var original = (byte[])buffer.Clone();
        var rect = new Rectangle(10, 8, 20, 16);
        var patch = new byte[rect.Width * rect.Height * Bpp];

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: true);

        // Simula o cursor sendo desenhado por cima.
        for (int y = 0; y < rect.Height; y++)
        {
            int offset = (rect.Y + y) * w * Bpp + rect.X * Bpp;
            for (int x = 0; x < rect.Width * Bpp; x++) buffer[offset + x] = 0xFF;
        }
        Assert.NotEqual(original, buffer);

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: false);

        Assert.Equal(original, buffer);
    }

    [Fact]
    public void RestoreTouchesNothingOutsideTheRectangle()
    {
        const int w = 40, h = 30;
        var buffer = Canvas(w, h);
        var rect = new Rectangle(5, 5, 10, 10);
        var patch = new byte[rect.Width * rect.Height * Bpp];

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: true);

        // Sujeira fora do retângulo tem de sobreviver à restauração: se o stride estivesse
        // errado, a devolução escreveria em linhas vizinhas e apagaria isto.
        int outside = (20 * w + 30) * Bpp;
        buffer[outside] = 0x7B;

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: false);

        Assert.Equal(0x7B, buffer[outside]);
    }

    [Fact]
    public void SavedPatchHoldsExactlyTheRectanglePixels()
    {
        const int w = 16, h = 16;
        var buffer = Canvas(w, h);
        var rect = new Rectangle(3, 4, 5, 6);
        var patch = new byte[rect.Width * rect.Height * Bpp];

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: true);

        for (int y = 0; y < rect.Height; y++)
        {
            for (int x = 0; x < rect.Width * Bpp; x++)
            {
                int fromBuffer = (rect.Y + y) * w * Bpp + rect.X * Bpp + x;
                int fromPatch = y * rect.Width * Bpp + x;
                Assert.Equal(buffer[fromBuffer], patch[fromPatch]);
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]     // canto superior esquerdo
    [InlineData(54, 38)]   // encostado no canto inferior direito
    public void WorksAtTheEdgesOfTheBuffer(int x, int y)
    {
        const int w = 64, h = 48;
        var buffer = Canvas(w, h);
        var original = (byte[])buffer.Clone();
        var rect = new Rectangle(x, y, 10, 10);
        var patch = new byte[rect.Width * rect.Height * Bpp];

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: true);
        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: false);

        Assert.Equal(original, buffer);
    }

    [Fact]
    public void ARectangleSpanningAWholeRowRoundTrips()
    {
        const int w = 32, h = 8;
        var buffer = Canvas(w, h);
        var original = (byte[])buffer.Clone();
        var rect = new Rectangle(0, 2, w, 3);
        var patch = new byte[rect.Width * rect.Height * Bpp];

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: true);
        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: false);

        Assert.Equal(original, buffer);
    }

    [Fact]
    public void APatchLargerThanNeededIsFine()
    {
        // O buffer do cursor é reaproveitado entre quadros, então costuma sobrar espaço de
        // um retângulo maior guardado antes.
        const int w = 32, h = 32;
        var buffer = Canvas(w, h);
        var original = (byte[])buffer.Clone();
        var rect = new Rectangle(4, 4, 6, 6);
        var patch = new byte[rect.Width * rect.Height * Bpp * 4];

        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: true);
        VideoCapturer.CopyRect(buffer, w, rect, patch, toPatch: false);

        Assert.Equal(original, buffer);
    }
}
