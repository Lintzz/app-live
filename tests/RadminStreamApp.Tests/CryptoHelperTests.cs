using System.Text;
using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

public class CryptoHelperTests
{
    [Fact]
    public void DeriveKey_IsDeterministicAndKeySized()
    {
        var a = CryptoHelper.DeriveKey("senha-da-sala");
        var b = CryptoHelper.DeriveKey("senha-da-sala");

        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void DeriveKey_DiffersPerPassword()
    {
        Assert.NotEqual(CryptoHelper.DeriveKey("senha-a"), CryptoHelper.DeriveKey("senha-b"));
    }

    [Fact]
    public void EncryptText_RoundTrips()
    {
        var key = CryptoHelper.DeriveKey("sala");
        var cipher = CryptoHelper.EncryptText("mensagem de sinalização", key);

        Assert.Equal("mensagem de sinalização", CryptoHelper.TryDecryptText(cipher, key));
    }

    [Fact]
    public void EncryptBytes_ProducesDifferentCiphertextEachTime()
    {
        var key = CryptoHelper.DeriveKey("sala");
        var plain = Encoding.UTF8.GetBytes("frame de audio");

        // Nonce aleatório por mensagem: dois envios do mesmo conteúdo não podem coincidir.
        Assert.NotEqual(CryptoHelper.EncryptBytes(plain, key), CryptoHelper.EncryptBytes(plain, key));
    }

    [Fact]
    public void TryDecrypt_RejectsWrongKey()
    {
        var cipher = CryptoHelper.EncryptBytes(Encoding.UTF8.GetBytes("segredo"), CryptoHelper.DeriveKey("certa"));

        Assert.Null(CryptoHelper.TryDecryptBytes(cipher, CryptoHelper.DeriveKey("errada")));
    }

    [Fact]
    public void TryDecrypt_RejectsTamperedPayload()
    {
        var key = CryptoHelper.DeriveKey("sala");
        var cipher = CryptoHelper.EncryptBytes(Encoding.UTF8.GetBytes("segredo"), key);

        // É isto que o AES-CBC sem autenticação não pegava: um byte trocado passava adiante.
        cipher[^1] ^= 0xFF;

        Assert.Null(CryptoHelper.TryDecryptBytes(cipher, key));
    }

    [Fact]
    public void TryDecrypt_RejectsTruncatedPayload()
    {
        Assert.Null(CryptoHelper.TryDecryptBytes(new byte[] { 1, 2, 3 }, CryptoHelper.DeriveKey("sala")));
    }

    [Fact]
    public void AuthProof_MatchesForSamePasswordAndChallenge()
    {
        var challenge = CryptoHelper.NewChallenge();

        var hostProof = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("abc"), challenge);
        var viewerProof = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("abc"), challenge);

        Assert.True(CryptoHelper.FixedTimeEquals(hostProof, viewerProof));
    }

    [Fact]
    public void AuthProof_DiffersForWrongPassword()
    {
        var challenge = CryptoHelper.NewChallenge();

        var expected = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("certa"), challenge);
        var attempt = CryptoHelper.ComputeAuthProof(CryptoHelper.DeriveKey("errada"), challenge);

        Assert.False(CryptoHelper.FixedTimeEquals(expected, attempt));
    }

    [Fact]
    public void AuthProof_DiffersPerChallenge()
    {
        var key = CryptoHelper.DeriveKey("abc");

        Assert.NotEqual(
            CryptoHelper.ComputeAuthProof(key, CryptoHelper.NewChallenge()),
            CryptoHelper.ComputeAuthProof(key, CryptoHelper.NewChallenge()));
    }

    [Fact]
    public void NewChallenge_IsUnique()
    {
        Assert.NotEqual(CryptoHelper.NewChallenge(), CryptoHelper.NewChallenge());
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    [InlineData(null, null)]
    public void FixedTimeEquals_HandlesNulls(string? a, string? b)
    {
        Assert.False(CryptoHelper.FixedTimeEquals(a, b));
    }
}
