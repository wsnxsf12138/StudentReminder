using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StudentReminder.WxLocal;

/// <summary>SQLCipher-4 primitives shared by the key extractor and the decryptor.</summary>
public static class WxCrypto
{
    /// <summary>Derive the per-database AES-256 key from a 32-byte passphrase and the database salt.</summary>
    public static byte[] DeriveEncKey(ReadOnlySpan<byte> passphrase, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, WxConstants.KdfIter, HashAlgorithmName.SHA512, WxConstants.KeySz);

    /// <summary>HMAC-SHA512 that page 1 must carry when <paramref name="encKey"/> is correct.</summary>
    public static byte[] HmacForPage1(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> page1)
    {
        var salt = page1[..WxConstants.SaltSz];
        Span<byte> macSalt = stackalloc byte[WxConstants.SaltSz];
        for (int i = 0; i < macSalt.Length; i++) macSalt[i] = (byte)(salt[i] ^ 0x3A);
        var macKey = Rfc2898DeriveBytes.Pbkdf2(encKey, macSalt, WxConstants.MacKdfIter,
            HashAlgorithmName.SHA512, WxConstants.KeySz);
        var body = page1[WxConstants.SaltSz..(WxConstants.PageSz - WxConstants.ReserveSz + WxConstants.IvSz)];
        Span<byte> pgno = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(pgno, 1);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA512, macKey);
        hmac.AppendData(body);
        hmac.AppendData(pgno);
        return hmac.GetHashAndReset();
    }

    /// <summary>Validate a candidate key against page 1 of an encrypted database.</summary>
    public static bool VerifyEncKey(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> page1)
    {
        if (page1.Length < WxConstants.PageSz) return false;
        var expected = page1[(WxConstants.PageSz - WxConstants.HmacSz)..WxConstants.PageSz];
        return CryptographicOperations.FixedTimeEquals(HmacForPage1(encKey, page1), expected);
    }

    /// <summary>Decrypt one 4096-byte page. Page 1 carries the salt prefix and regains the SQLite header.</summary>
    public static byte[] DecryptPage(ReadOnlySpan<byte> encKey, ReadOnlySpan<byte> page, int pgno)
    {
        if (page.Length < WxConstants.PageSz)
        {
            var padded = new byte[WxConstants.PageSz];
            page.CopyTo(padded);
            return DecryptPage(encKey, padded, pgno);
        }
        var iv = page[(WxConstants.PageSz - WxConstants.ReserveSz)..(WxConstants.PageSz - WxConstants.ReserveSz + WxConstants.IvSz)];
        var body = pgno == 1
            ? page[WxConstants.SaltSz..(WxConstants.PageSz - WxConstants.ReserveSz)]
            : page[..(WxConstants.PageSz - WxConstants.ReserveSz)];
        using var aes = Aes.Create();
        aes.Key = encKey.ToArray();
        var dec = aes.DecryptCbc(body, iv, PaddingMode.None);
        var result = new byte[WxConstants.PageSz];
        if (pgno == 1)
        {
            WxConstants.SqliteHeader.CopyTo(result);
            dec.CopyTo(result.AsSpan(WxConstants.SaltSz));
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
        if (key.Length != WxConstants.KeySz) return false;
        var distinct = new HashSet<byte>(key.ToArray());
        return distinct.Count >= 15 && !(key.TrimEnd((byte)0).Length == 0 || key.TrimEnd((byte)0xFF).Length == 0);
    }
}
