using System.IO;
using Microsoft.Win32;

namespace CursorTranslator.Services;

public sealed class StartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CursorTranslator";

    public bool IsEnabled
    {
        get
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return runKey?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (runKey is null)
            throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");

        if (!enabled)
        {
            runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = ResolveExecutablePath();
        if (!File.Exists(executablePath))
            throw new FileNotFoundException("找不到 Cheems翻译程序，无法设置开机启动。", executablePath);

        runKey.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }

    private static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && Path.GetFileName(processPath).Equals("CursorTranslator.exe", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(processPath);

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "CursorTranslator.exe"));
    }
}
