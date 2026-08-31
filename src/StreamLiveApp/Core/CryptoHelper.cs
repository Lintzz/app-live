using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace StreamLiveApp
{
    /// <summary>
    /// Criptografia da sala. Duas mudanças em relação à primeira versão:
    /// a chave sai de um PBKDF2 (e não de um SHA256 cru, que era força-bruta trivial),
    /// e o payload é AES-GCM — autenticado, então mensagem adulterada é rejeitada
    /// em vez de "descriptografar" em lixo.
    /// </summary>
    public static class CryptoHelper
    {
        // Salt fixo da aplicação: a chave é derivada uma vez por sala e reaproveitada em
        // todos os frames, então não dá para carregar um salt aleatório por mensagem.
        // Com 200k iterações o ataque de dicionário fica caro mesmo com salt conhecido.
        // O texto do salt é o nome antigo do app e ficou como estava de propósito: mudá-lo
        // troca a chave derivada, e host atualizado com viewer desatualizado (ou o contrário)
        // passaria a recusar toda senha de sala até os dois lados estarem na mesma versão.
        private static readonly byte[] AppSalt = Encoding.UTF8.GetBytes("RadminStreamLive::room-key::v2");
        private const int Pbkdf2Iterations = 200_000;

        private const int NonceSize = 12;  // AES-GCM padrão
        private const int TagSize = 16;

        // PBKDF2 a 200k iterações custa ~100ms; o áudio chama isso ~50x/s.
        // Sem cache o app trava, então a chave derivada fica guardada por senha.
        private static readonly ConcurrentDictionary<string, byte[]> KeyCache = new();

        public static byte[] DeriveKey(string password)
        {
            return KeyCache.GetOrAdd(password ?? string.Empty, static pwd =>
                Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(pwd),
                    AppSalt,
                    Pbkdf2Iterations,
                    HashAlgorithmName.SHA256,
                    32));
        }

        public static string EncryptText(string plainText, byte[] key)
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var cipherBytes = EncryptBytes(plainBytes, key);
            return Convert.ToBase64String(cipherBytes);
        }

        public static string? TryDecryptText(string cipherTextB64, byte[] key)
        {
            try
            {
                var cipherBytes = Convert.FromBase64String(cipherTextB64);
                var plainBytes = TryDecryptBytes(cipherBytes, key);
                if (plainBytes == null) return null;
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Formato: [nonce 12][tag 16][ciphertext].</summary>
        public static byte[] EncryptBytes(byte[] plain, byte[] key)
        {
            var result = new byte[NonceSize + TagSize + plain.Length];
            var nonce = result.AsSpan(0, NonceSize);
            var tag = result.AsSpan(NonceSize, TagSize);
            var cipher = result.AsSpan(NonceSize + TagSize);

            RandomNumberGenerator.Fill(nonce);
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag);
            return result;
        }

        /// <summary>Devolve null quando a tag não confere — payload adulterado ou chave errada.</summary>
        public static byte[]? TryDecryptBytes(byte[] cipher, byte[] key)
        {
            try
            {
                if (cipher == null || cipher.Length < NonceSize + TagSize) return null;

                var nonce = cipher.AsSpan(0, NonceSize);
                var tag = cipher.AsSpan(NonceSize, TagSize);
                var payload = cipher.AsSpan(NonceSize + TagSize);

                var plain = new byte[payload.Length];
                using var aes = new AesGcm(key, TagSize);
                aes.Decrypt(nonce, payload, tag, plain);
                return plain;
            }
            catch
            {
                return null;
            }
        }

        // ───────────────────────── Autenticação por desafio ─────────────────────────

        /// <summary>Desafio aleatório que o host manda junto do AUTH_REQUIRED.</summary>
        public static string NewChallenge()
        {
            var nonce = new byte[32];
            RandomNumberGenerator.Fill(nonce);
            return Convert.ToBase64String(nonce);
        }

        /// <summary>
        /// Prova de que o viewer conhece a senha, sem mandar a senha no fio: HMAC do desafio
        /// com a chave derivada. Antes o AUTH carregava a senha em texto claro sobre ws://,
        /// então qualquer um na VPN a lia.
        /// </summary>
        public static string ComputeAuthProof(byte[] key, string challenge)
        {
            using var hmac = new HMACSHA256(key);
            var mac = hmac.ComputeHash(Encoding.UTF8.GetBytes(challenge ?? string.Empty));
            return Convert.ToBase64String(mac);
        }

        /// <summary>Comparação em tempo constante: evita distinguir senhas pelo tempo de resposta.</summary>
        public static bool FixedTimeEquals(string? a, string? b)
        {
            if (a == null || b == null) return false;

            var bytesA = Encoding.UTF8.GetBytes(a);
            var bytesB = Encoding.UTF8.GetBytes(b);

            // FixedTimeEquals exige tamanhos iguais; normalizamos por hash para não
            // vazar o comprimento nem cair no atalho de tamanho diferente.
            Span<byte> hashA = stackalloc byte[32];
            Span<byte> hashB = stackalloc byte[32];
            SHA256.HashData(bytesA, hashA);
            SHA256.HashData(bytesB, hashB);

            return CryptographicOperations.FixedTimeEquals(hashA, hashB);
        }
    }
}
