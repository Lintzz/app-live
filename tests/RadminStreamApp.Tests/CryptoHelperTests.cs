using System.Text;
using RadminStreamApp;
using Xunit;

namespace RadminStreamApp.Tests;

public class CryptoHelperTests
{
    [Fact]
    public void TextRoundTripsWithTheSamePassword()
    {
        var key = CryptoHelper.DeriveKey("sala-secreta");

        var cipher = CryptoHelper.EncryptText("oi, mundo", key);

        Assert.NotEqual("oi, mundo", cipher);
        Assert.Equal("oi, mundo", CryptoHelper.TryDecryptText(cipher, key));
    }

    [Fact]
    public void BytesRoundTrip()
    {
        var key = CryptoHelper.DeriveKey("sala-secreta");
        var plain = Encoding.UTF8.GetBytes("pacote de áudio qualquer");

        var decrypted = CryptoHelper.TryDecryptBytes(CryptoHelper.EncryptBytes(plain, key), key);

        Assert.Equal(plain, decrypted);
    }

    [Fact]
    public void WrongPasswordDoesNotDecrypt()
    {
        var cipher = CryptoHelper.EncryptText("segredo", CryptoHelper.DeriveKey("certa"));

        Assert.Null(CryptoHelper.TryDecryptText(cipher, CryptoHelper.DeriveKey("errada")));
    }

    /// <summary>
    /// A razão de o payload ser AES-GCM e não AES cru: mensagem adulterada é rejeitada em
    /// vez de "descriptografar" em lixo que o resto do app trataria como dado válido.
    /// </summary>
    [Fact]
    public void TamperedPayloadIsRejected()
    {
        var key = CryptoHelper.DeriveKey("sala-secreta");
        var cipher = CryptoHelper.EncryptBytes(Encoding.UTF8.GetBytes("conteúdo"), key);

        cipher[^1] ^= 0xFF; // um bit trocado no fim do ciphertext

        Assert.Null(CryptoHelper.TryDecryptBytes(cipher, key));
    }

    [Fact]
    public void TruncatedPayloadIsRejectedInsteadOfThrowing()
    {
        var key = CryptoHelper.DeriveKey("sala-secreta");

        // Menor que nonce + tag: vem da rede, então não pode estourar exceção.
        Assert.Null(CryptoHelper.TryDecryptBytes(new byte[4], key));
        Assert.Null(CryptoHelper.TryDecryptBytes(System.Array.Empty<byte>(), key));
        Assert.Null(CryptoHelper.TryDecryptText("isto não é base64 válido!!", key));
    }

    [Fact]
    public void SameNonceIsNeverReused()
    {
        var key = CryptoHelper.DeriveKey("sala-secreta");
        var plain = Encoding.UTF8.GetBytes("mesma mensagem");

        var a = CryptoHelper.EncryptBytes(plain, key);
        var b = CryptoHelper.EncryptBytes(plain, key);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void DeriveKeyIsDeterministicAndPasswordSpecific()
    {
        Assert.Equal(CryptoHelper.DeriveKey("abc"), CryptoHelper.DeriveKey("abc"));
        Assert.NotEqual(CryptoHelper.DeriveKey("abc"), CryptoHelper.DeriveKey("abd"));
        Assert.Equal(32, CryptoHelper.DeriveKey("abc").Length);
    }

    [Fact]
    public void ChallengesAreUnique()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 50; i++)
        {
            Assert.True(seen.Add(CryptoHelper.NewChallenge()), "desafio repetido");
        }
    }

    [Fact]
    public void AuthProofDependsOnBothPasswordAndChallenge()
    {
        var right = CryptoHelper.DeriveKey("certa");
        var wrong = CryptoHelper.DeriveKey("errada");
        var challenge = CryptoHelper.NewChallenge();

        var expected = CryptoHelper.ComputeAuthProof(right, challenge);

        Assert.Equal(expected, CryptoHelper.ComputeAuthProof(right, challenge));
        Assert.NotEqual(expected, CryptoHelper.ComputeAuthProof(wrong, challenge));
        Assert.NotEqual(expected, CryptoHelper.ComputeAuthProof(right, CryptoHelper.NewChallenge()));
    }

    [Fact]
    public void FixedTimeEqualsHandlesNullsAndDifferentLengths()
    {
        Assert.True(CryptoHelper.FixedTimeEquals("igual", "igual"));
        Assert.False(CryptoHelper.FixedTimeEquals("igual", "diferente"));
        Assert.False(CryptoHelper.FixedTimeEquals(null, "x"));
        Assert.False(CryptoHelper.FixedTimeEquals("x", null));
        Assert.False(CryptoHelper.FixedTimeEquals(null, null));
    }
}
