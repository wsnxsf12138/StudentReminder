namespace StudentReminder.QqLocal;

/// <summary>QQ NT (SQLCipher 4) database layout and cryptography constants.</summary>
public static class QqConstants
{
    /// <summary>Custom QQ header prepended before the SQLCipher stream.</summary>
    public const int HeaderSz = 1024;

    public const int PageSz = 4096;
    public const int KeySz = 32;
    public const int SaltSz = 16;
    public const int IvSz = 16;
    public const int HmacSz = 20;                // HMAC-SHA1
    public const int ReserveSz = 48;             // IV(16) + HMAC-SHA1(20) + 12-byte alignment padding
    public const int KdfIter = 4000;             // PBKDF2-HMAC-SHA512 iterations
    public const int MacKdfIter = 2;

    public const uint WalMagic1 = 0x377f0682;
    public const uint WalMagic2 = 0x377f0683;
    public const int WalHdrSz = 32;
    public const int WalFrameHdrSz = 24;

    public const int MaxPassphraseAttempts = 16;

    public static readonly byte[] SqliteHeader = "SQLite format 3\u0000"u8.ToArray();
}
