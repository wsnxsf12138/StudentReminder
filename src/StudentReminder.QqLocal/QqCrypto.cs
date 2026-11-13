using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StudentReminder.QqLocal;

/// <summary>SQLCipher-4 primitives for QQ NT (HMAC-SHA1 page MAC, 36-byte reserve).</summary>
public static class QqCrypto
{
    /// <summary>Derive the per-database AES-256 key from a 16-byte passphrase and the database salt.</summary>
    public static byte[] DeriveEncKey(ReadOnlySpan<byte> passphrase, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, QqConstants.KdfIter, HashAlgorithmName.SHA512, QqConstants.KeySz);

    /// <summary>HMAC-SHA1 that page 1 must carry when <paramref name="encKey"/> is correct.</summary>
    public static byte[] HmacForPage1(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> page1)
    {
        var salt = page1[..QqConstants.SaltSz];
        Span<byte> macSalt = stackalloc byte[QqConstants.SaltSz];
        for (int i = 0; i < macSalt.Length; i++) macSalt[i] = (byte)(salt[i] ^ 0x3A);
        var macKey = Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, QqConstants.MacKdfIter,
            HashAlgorithmName.SHA512, QqConstants.KeySz);
        var hmacOffset = QqConstants.PageSz - QqConstants.ReserveSz + QqConstants.IvSz;
        var body = page1[QqConstants.SaltSz..hmacOffset];
        Span<byte> pgno = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pgno, 1);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, macKey);
        hmac.AppendData(body);
        hmac.AppendData(pgno);
        return hmac.GetHashAndReset();
    }

    /// <summary>Validate a candidate key against page 1 of an encrypted database.</summary>
    public static bool VerifyEncKey(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> page1)
    {
        if (page1.Length < QqConstants.PageSz) return false;
        var hmacOffset = QqConstants.PageSz - QqConstants.ReserveSz + QqConstants.IvSz;
        var expected = page1[hmacOffset..(hmacOffset + QqConstants.HmacSz)];
        return CryptographicOperations.FixedTimeEquals(HmacForPage1(encKey, page1), expected);
    }

    /// <summary>Decrypt one 4096-byte page. Page 1 regains the SQLite header over the salt region.</summary>
    public static byte[] DecryptPage(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> page, int pgno)
    {
        if (page.Length < QqConstants.PageSz)
        {
            var padded = new byte[QqConstants.PageSz];
            page.CopyTo(padded);
            return DecryptPage(encKey, padded, pgno);
        }
        var iv = page[(QqConstants.PageSz - QqConstants.ReserveSz)..(QqConstants.PageSz - QqConstants.ReserveSz + QqConstants.IvSz)];
        var body = pgno == 1
            ? page[QqConstants.SaltSz..(QqConstants.PageSz - QqConstants.ReserveSz)]
            : page[..(QqConstants.PageSz - QqConstants.ReserveSz)];
        using var aes = Aes.Create();
        aes.Key = encKey.ToArray();
        var dec = aes.DecryptCbc(body, iv, PaddingMode.None);
        var result = new byte[QqConstants.PageSz];
        if (pgno == 1)
        {
            QqConstants.SqliteHeader.CopyTo(result);
            dec.CopyTo(result.AsSpan(QqConstants.SaltSz));
        }
        else
        {
            dec.CopyTo(result);
        }
        return result;
    }

    /// <summary>Heuristic filter for key candidates: 32 bytes, enough entropy, not all-zero or all-0xFF.</summary>
    public static bool LooksLikeKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != QqConstants.KeySz) return false;
        var distinct = new HashSet<byte>(key.ToArray());
        return distinct.Count >= 15 && !(key.TrimEnd((byte)0).Length == 0 || key.TrimEnd((byte)0xFF).Length == 0);
    }
}
