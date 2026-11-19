using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ThreeDBuilder.Core.Services;

/// <summary>
/// Encryption for config.enc (settings + BYOK API keys).
/// AES-256-GCM with a PBKDF2-derived key from a user vault password.
/// Nothing is ever stored in plaintext on disk.
/// </summary>
public static class EncryptionService
{
    private const int Pbkdf2Iterations = 100_000;
    private const int KeySize = 32; // 256 bits
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Encrypt a UTF-8 string to a base64 blob: salt || nonce || tag || ciphertext</summary>
    public static string Encrypt(string plaintext, string password)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        using var rng = RandomNumberGenerator.Create();
        byte[] salt = new byte[SaltSize];
        rng.GetBytes(salt);
        byte[] nonce = new byte[NonceSize];
        rng.GetBytes(nonce);

        byte[] key = DeriveKey(password, salt);
        using var aes = new AesGcm(key);
        byte[] plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        byte[] ciphertext = new byte[plaintextBytes.Length];
        byte[] tag = new byte[TagSize];
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        using var ms = new MemoryStream();
        ms.Write(salt, 0, salt.Length);
        ms.Write(nonce, 0, nonce.Length);
        ms.Write(tag, 0, tag.Length);
        ms.Write(ciphertext, 0, ciphertext.Length);
        return Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Decrypt a base64 blob produced by Encrypt.</summary>
    public static string Decrypt(string ciphertextBase64, string password)
    {
        if (string.IsNullOrEmpty(ciphertextBase64)) return string.Empty;
        byte[] data = Convert.FromBase64String(ciphertextBase64);
        if (data.Length < SaltSize + NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short.");

        byte[] salt = new byte[SaltSize];
        byte[] nonce = new byte[NonceSize];
        byte[] tag = new byte[TagSize];
        Array.Copy(data, 0, salt, 0, SaltSize);
        Array.Copy(data, SaltSize, nonce, 0, NonceSize);
        Array.Copy(data, SaltSize + NonceSize, tag, 0, TagSize);
        byte[] ciphertext = new byte[data.Length - SaltSize - NonceSize - TagSize];
        Array.Copy(data, SaltSize + NonceSize + TagSize, ciphertext, 0, ciphertext.Length);

        byte[] key = DeriveKey(password, salt);
        using var aes = new AesGcm(key);
        byte[] plaintext = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Derive an AES-256 key from a password + salt via PBKDF2-HMAC-SHA256.</summary>
    private static byte[] DeriveKey(string password, byte[] salt)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Pbkdf2Iterations, HashName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
