using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StudentReminder.WxLocal;

public sealed record WxAccount(string DirName, string Wxid, string AccountPath, string DbStorage);

/// <summary>Locates the WeChat 4.x installation, data root and per-account db_storage directories.</summary>
public static partial class WxPaths
{
    static WxPaths() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string DirName = "xwechat_files";
    private const string StorageName = "db_storage";

    [GeneratedRegex(@"^(.+?)_([0-9a-fA-F]{4})$")]
    private static partial Regex WxidPattern();

    public static string? GetInstallPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Tencent\Weixin");
        if (key?.GetValue("InstallPath") is string path && Directory.Exists(path)) return path;
        return null;
    }

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

    private static IEnumerable<string> RootsFromConfig()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tencent", "xwechat", "config");
        if (!Directory.Exists(configDir)) yield break;
        foreach (var ini in Directory.EnumerateFiles(configDir, "*.ini"))
        {
            string? raw = null;
            foreach (var encoding in new[] { Encoding.UTF8, Encoding.GetEncoding("GBK") })
            {
                try { raw = File.ReadAllText(ini, encoding).Trim(); break; }
                catch (DecoderFallbackException) { }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
            }
            if (string.IsNullOrEmpty(raw)) continue;
            if (raw.TrimEnd('\\', '/') == "MyDocument:") yield return DocumentsDir();
            else if (Directory.Exists(raw)) yield return raw;
        }
    }

    private static IEnumerable<string> RootsFromRegistry()
    {
        foreach (var subKey in new[] { @"Software\Tencent\Weixin", @"Software\Tencent\WeChat" })
        {
            var value = ReadRegistryValue(Registry.CurrentUser, subKey, "FileSavePath");
            if (value is null) continue;
            value = Environment.ExpandEnvironmentVariables(value);
            if (value.TrimEnd('\\', '/') == "MyDocument:") yield return DocumentsDir();
            else if (Directory.Exists(value)) yield return value;
        }
    }

    private static IEnumerable<string> QuickScanDrives()
    {
        var found = new List<string>();
        foreach (var letter in "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
        {
            var root = $"{letter}:\\";
            if (!Directory.Exists(root)) continue;
            var candidates = new List<string> { root };
            foreach (var baseName in new[] { "Users", "Documents", "WeChat", "Tencent", "ProgramData" })
            {
                var path = Path.Combine(root, baseName);
                if (Directory.Exists(path)) candidates.Add(path);
            }
            foreach (var candidate in candidates)
            {
                try
                {
                    foreach (var entry in Directory.EnumerateDirectories(candidate))
                    {
                        if (Path.GetFileName(entry) == DirName) { found.Add(candidate); continue; }
                        if (Directory.Exists(Path.Combine(entry, DirName))) found.Add(entry);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return found.Distinct();
    }

    /// <summary>Locate the xwechat_files directory itself, or null when not found.</summary>
    public static string? GetXwechatRoot()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bases = RootsFromConfig()
            .Concat(RootsFromRegistry())
            .Append(DocumentsDir())
            .Concat(QuickScanDrives());
        foreach (var basePath in bases)
        {
            if (!seen.Add(Path.GetFullPath(basePath))) continue;
            if (string.Equals(Path.GetFileName(basePath.TrimEnd('\\', '/')), DirName, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(basePath)) return basePath;
            var candidate = Path.Combine(basePath, DirName);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Split the account directory name wxid_xxx_ab12 into its wxid part.</summary>
    public static string SplitWxid(string dirName)
    {
        var match = WxidPattern().Match(dirName);
        return match.Success ? match.Groups[1].Value : dirName;
    }

    /// <summary>List every account directory under xwechat_files that has a db_storage folder.</summary>
    public static IReadOnlyList<WxAccount> ListAccountDirs()
    {
        var root = GetXwechatRoot();
        if (root is null) return [];
        var accounts = new List<(WxAccount Account, long Mtime)>();
        foreach (var full in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(full);
            if (name.Equals("all_users", StringComparison.OrdinalIgnoreCase)) continue;
            var storage = Path.Combine(full, StorageName);
            if (!Directory.Exists(storage)) continue;
            long mtime;
            try
            {
                mtime = Directory.EnumerateFiles(storage, "*.db", SearchOption.AllDirectories)
                    .Select(path => { try { return File.GetLastWriteTimeUtc(path).Ticks; } catch (IOException) { return 0L; } })
                    .DefaultIfEmpty(0).Max();
            }
            catch (IOException) { mtime = 0; }
            catch (UnauthorizedAccessException) { mtime = 0; }
            accounts.Add((new WxAccount(name, SplitWxid(name), full, storage), mtime));
        }
        return accounts.OrderByDescending(item => item.Mtime).Select(item => item.Account).ToList();
    }

    /// <summary>Accounts that have logged in on this machine (all_users\login\&lt;wxid&gt;\key_info.db).</summary>
    public static IReadOnlyList<string> ListLoggedInWxids()
    {
        var root = GetXwechatRoot();
        if (root is null) return [];
        var login = Path.Combine(root, "all_users", "login");
        if (!Directory.Exists(login)) return [];
        return Directory.EnumerateDirectories(login)
            .Where(dir => File.Exists(Path.Combine(dir, "key_info.db")))
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Find an account by wxid; without a wxid the most recently used account is returned.</summary>
    public static WxAccount? FindAccount(string? wxid = null)
    {
        var accounts = ListAccountDirs();
        if (accounts.Count == 0) return null;
        if (string.IsNullOrEmpty(wxid)) return accounts[0];
        return accounts.FirstOrDefault(a => a.Wxid == wxid || a.DirName == wxid)
            ?? accounts.FirstOrDefault(a => a.Wxid.StartsWith(wxid, StringComparison.Ordinal)
                || wxid.StartsWith(a.Wxid, StringComparison.Ordinal));
    }
}
