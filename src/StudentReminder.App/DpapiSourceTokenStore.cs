using System.IO;
using System.Security.Cryptography;
using System.Text;
using StudentReminder.Core;

namespace StudentReminder.App;

public sealed class DpapiSourceTokenStore(string dataDirectory) : ISourceTokenStore
{
    private string TokenPath(string sourceId)
    {
        if (sourceId.Length != 64 || sourceId.Any(character => !Uri.IsHexDigit(character))) throw new ArgumentException("无效来源编号。");
        return Path.Combine(dataDirectory, "secrets", sourceId + ".dpapi");
    }

    public async Task<string?> ReadAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var path = TokenPath(sourceId);
        if (!File.Exists(path)) return null;
        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        var clear = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(clear); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    public async Task WriteAsync(string sourceId, string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsWhiteSpace)) throw new ArgumentException("无效 Token。");
        var path = TokenPath(sourceId);
        var clear = Encoding.UTF8.GetBytes(token);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, encrypted, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
