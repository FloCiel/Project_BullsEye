using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace BullsEye.Core;

/// <summary>
/// PIN 기반 암호화: Argon2id로 키를 유도하고 AES-256-GCM으로 봉인한다.
/// </summary>
/// <remarks>
/// 레이아웃: magic "BEYE"(4) | version(1) | memoryKiB(4) | iterations(4) | parallelism(4)
///          | salt(16) | nonce(12) | tag(16) | ciphertext.
/// tag 앞까지의 헤더는 AAD로 인증되어 파라미터 변조도 감지된다.
/// </remarks>
public static class SecretBox
{
    private static readonly byte[] Magic = "BEYE"u8.ToArray();
    private const byte Version = 1;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int HeaderSize = 4 + 1 + 4 + 4 + 4 + SaltSize + NonceSize;
    private const int MaxMemoryKiB = 1024 * 1024;
    private const int MaxIterations = 20;
    private const int MaxParallelism = 16;

    public static byte[] Seal(byte[] plaintext, string pin, KdfParameters kdf)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);

        var output = new byte[HeaderSize + TagSize + plaintext.Length];
        var header = output.AsSpan(0, HeaderSize);
        Magic.CopyTo(header);
        header[4] = Version;
        BinaryPrimitives.WriteInt32LittleEndian(header[5..], kdf.MemoryKiB);
        BinaryPrimitives.WriteInt32LittleEndian(header[9..], kdf.Iterations);
        BinaryPrimitives.WriteInt32LittleEndian(header[13..], kdf.Parallelism);
        salt.CopyTo(header[17..]);
        nonce.CopyTo(header[(17 + SaltSize)..]);

        var key = DeriveKey(pin, salt, kdf);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext,
                output.AsSpan(HeaderSize + TagSize),
                output.AsSpan(HeaderSize, TagSize),
                header);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return output;
    }

    public static byte[] Open(byte[] sealedBytes, string pin)
    {
        if (sealedBytes.Length < HeaderSize + TagSize) throw new CorruptSettingsException("too short");

        var header = sealedBytes.AsSpan(0, HeaderSize);
        if (!header[..4].SequenceEqual(Magic)) throw new CorruptSettingsException("not a BullsEye file");
        if (header[4] != Version) throw new CorruptSettingsException($"unknown version {header[4]}");

        var kdf = new KdfParameters(
            BinaryPrimitives.ReadInt32LittleEndian(header[5..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[9..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[13..]));
        // 인증 전에 읽는 값이므로 범위를 제한한다 (메모리 폭주 방지).
        if (kdf.MemoryKiB is < 8 or > MaxMemoryKiB
            || kdf.Iterations is < 1 or > MaxIterations
            || kdf.Parallelism is < 1 or > MaxParallelism
            || kdf.MemoryKiB < 8 * kdf.Parallelism)
        {
            throw new CorruptSettingsException("invalid KDF parameters");
        }

        var salt = header.Slice(17, SaltSize).ToArray();
        var nonce = header.Slice(17 + SaltSize, NonceSize);
        var tag = sealedBytes.AsSpan(HeaderSize, TagSize);
        var ciphertext = sealedBytes.AsSpan(HeaderSize + TagSize);

        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(pin, salt, kdf);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, header);
        }
        catch (AuthenticationTagMismatchException ex)
        {
            throw new WrongPinException(ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return plaintext;
    }

    private static byte[] DeriveKey(string pin, byte[] salt, KdfParameters kdf)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(pin))
        {
            Salt = salt,
            MemorySize = kdf.MemoryKiB,
            Iterations = kdf.Iterations,
            DegreeOfParallelism = kdf.Parallelism,
        };
        return argon2.GetBytes(KeySize);
    }
}
