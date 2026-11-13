using System.IO;
using Microsoft.Win32;

namespace StudentReminder.App;

internal static class LoginStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "StudentReminder";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(EntryName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(EntryName, throwOnMissingValue: false);
            return;
        }
        var executable = Path.Combine(AppContext.BaseDirectory, "StudentReminder.App.exe");
        var command = $"\"{executable}\" --startup";
        if (!File.Exists(executable) || command.Length > 260)
            throw new InvalidOperationException("找不到可注册的程序，或程序路径超过 Windows 登录启动长度限制。");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(EntryName, command, RegistryValueKind.String);
    }
}
