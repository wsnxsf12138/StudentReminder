using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StudentReminder.QqLocal;

public sealed record QqAccount(string Qq, string AccountPath, string NtDbDir);

/// <summary>Locates per-account QQ NT database directories under the user's Documents.</summary>
public static partial class QqPaths
{
    private const string NtDbDirName = "nt_db";

    [GeneratedRegex(@"^\d{5,12}$")]
    private static partial Regex QqNumberPattern();

    /// <summary>Resolve the user's real Documents directory (handles OneDrive redirection).</summary>
    private static string DocumentsDir()
    {
        var value = ReadRegistryValue(Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", "Personal");
        if (value is not null)
        {
            var expanded = Environment.ExpandEnvironmentVariables(value);
            if (Directory.Exists(expanded)) return expanded;
        }
        value = ReadRegistryValue(Registry.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", "Personal");
        if (value is not null && Directory.Exists(value)) return value;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
    }

    private static string? ReadRegistryValue(RegistryKey root, string subKey, string name)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The Tencent Files root, or null when not present.</summary>
    public static string? GetTencentFilesRoot()
    {
        var candidate = Path.Combine(DocumentsDir(), "Tencent Files");
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>List every account (numeric dir) that carries a nt_qq\nt_db folder.</summary>
    public static IReadOnlyList<QqAccount> ListAccounts()
    {
        var root = GetTencentFilesRoot();
        if (root is null) return [];
        var accounts = new List<(QqAccount Account, long Mtime)>();
        foreach (var full in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(full);
            if (!QqNumberPattern().IsMatch(name)) continue;
            var ntDb = Path.Combine(full, "nt_qq", NtDbDirName);
            if (!Directory.Exists(ntDb)) continue;
            long mtime;
            try
            {
                mtime = Directory.EnumerateFiles(ntDb, "*.db", SearchOption.AllDirectories)
                    .Select(path => { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch (IOException) { return 0L; } })
                    .DefaultIfEmpty(0).Max();
            }
            catch (IOException) { mtime = 0; }
            catch (UnauthorizedAccessException) { mtime = 0; }
            accounts.Add((new QqAccount(name, full, ntDb), mtime));
        }
        return accounts.OrderByDescending(item => item.Mtime).Select(item => item.Account).ToList();
    }

    /// <summary>Find an account by QQ number; without one the most recently used account is returned.</summary>
    public static QqAccount? FindAccount(string? qq = null)
    {
        var accounts = ListAccounts();
        if (accounts.Count == 0) return null;
        if (string.IsNullOrEmpty(qq)) return accounts[0];
        return accounts.FirstOrDefault(account => account.Qq == qq)
            ?? accounts.FirstOrDefault(account => account.Qq.StartsWith(qq, StringComparison.Ordinal)
                || qq.StartsWith(account.Qq, StringComparison.Ordinal));
    }
}
