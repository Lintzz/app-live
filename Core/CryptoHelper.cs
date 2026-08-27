using System;
using System.Security.Cryptography;
using System.Text;

namespace RadminStreamApp
{
    public static class CryptoHelper
    {
        public static byte[] DeriveKey(string password)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(password ?? string.Empty));
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

        public static byte[] EncryptBytes(byte[] plain, byte[] key)
        {
            using var aes = Aes.Create();
            aes.Key = key;
            aes.GenerateIV();
            using var encryptor = aes.CreateEncryptor();
            var cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

            var result = new byte[aes.IV.Length + cipher.Length];
            Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
            Buffer.BlockCopy(cipher, 0, result, aes.IV.Length, cipher.Length);
            return result;
        }

        public static byte[]? TryDecryptBytes(byte[] cipher, byte[] key)
        {
            try
            {
                using var aes = Aes.Create();
                aes.Key = key;
                var ivLength = aes.BlockSize / 8;
                if (cipher.Length < ivLength) return null;

                var iv = new byte[ivLength];
                Buffer.BlockCopy(cipher, 0, iv, 0, ivLength);
                aes.IV = iv;

                using var decryptor = aes.CreateDecryptor();
                return decryptor.TransformFinalBlock(cipher, ivLength, cipher.Length - ivLength);
            }
            catch
            {
                return null;
            }
        }
    }
}
