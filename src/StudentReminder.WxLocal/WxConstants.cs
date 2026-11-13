namespace StudentReminder.WxLocal;

/// <summary>WeChat 4.x (WCDB / SQLCipher 4) database layout constants.</summary>
public static class WxConstants
{
    public const int PageSz = 4096;
    public const int KeySz = 32;
    public const int SaltSz = 16;
    public const int IvSz = 16;
    public const int HmacSz = 64;
    public const int ReserveSz = 80;              // IV(16) + HMAC(64)
    public const int KdfIter = 256000;            // PBKDF2-HMAC-SHA512 iterations
    public const int MacKdfIter = 2;
    public const int MaxUserAddressHigh = 0x0000_8000; // high 32 bits of 0x0000_8000_0000_0000

    public const uint WalMagic1 = 0x377f0682;
    public const uint WalMagic2 = 0x377f0683;
    public const int WalHdrSz = 32;
    public const int WalFrameHdrSz = 24;

    public static readonly byte[] SqliteHeader = "SQLite format 3\u0000"u8.ToArray();
    public static readonly byte[] CipherNeedle = "com.Tencent.WCDB.Config.Cipher"u8.ToArray();

    /// <summary>Fixed XOR mask applied to the Config.Cipher key blob (WeChat 4.1.11+ layout).</summary>
    public static readonly byte[] CipherXorMask = Convert.FromHexString(
        "d2c7442458020000004889442450488b450048844c2448488944254048584c24");

    public const int ConfigBlobMax = 1024;
    public const int MaxPassphraseAttempts = 64;
}
