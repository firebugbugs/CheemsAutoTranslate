using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CursorTranslator.Services;

internal static class UpdateInstallerHandoff
{
    internal sealed record ProcessIdentity(int Id, long StartTimeUtcTicks, string Path);

    internal static void Start(string installer, string targetExe, string fallbackExe, int parentId,
        string partial, string metadata, string log)
    {
        if (!File.Exists(installer)) throw new FileNotFoundException("更新安装包不存在。", installer);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        using var parent = Process.GetProcessById(parentId);
        var sourcePath = parent.MainModule?.FileName
            ?? throw new InvalidOperationException("无法核实待更新进程的路径。");
        var identities = new List<ProcessIdentity>();
        foreach (var candidate in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(sourcePath)))
        {
            using (candidate)
            {
                try
                {
                    var path = candidate.MainModule?.FileName;
                    if (path is not null && string.Equals(path, sourcePath, StringComparison.OrdinalIgnoreCase))
                        identities.Add(new(candidate.Id, candidate.StartTime.ToUniversalTime().Ticks, path));
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        if (identities.All(identity => identity.Id != parentId))
            throw new InvalidOperationException("无法核实待更新进程的身份。");

        var eventName = @"Local\CursorTranslator.UpdateReady." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var helper = Process.Start(CreateStartInfo(installer, targetExe, fallbackExe,
            partial, metadata, log, identities, eventName))
            ?? throw new InvalidOperationException("无法启动更新安装进程。");
        // Do not close the application until the helper has acquired the update
        // lock and is ready to supervise shutdown independently of this process.
        var deadline = Stopwatch.StartNew();
        while (!ready.WaitOne(100))
        {
            if (helper.HasExited || deadline.Elapsed > TimeSpan.FromSeconds(5))
            {
                if (!helper.HasExited) helper.Kill();
                throw new InvalidOperationException("更新助手未能就绪，可能已有安装正在进行。请查看更新日志。");
            }
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string installer, string targetExe, string fallbackExe,
        string partial, string metadata, string log, IReadOnlyList<ProcessIdentity> processes,
        string readyEvent, int shutdownTimeoutMilliseconds = 10_000)
    {
        var lockHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(targetExe).ToUpperInvariant())));
        var payload = JsonSerializer.Serialize(new
        {
            installer, targetExe, fallbackExe, partial, metadata, log, processes, readyEvent,
            shutdownTimeoutMilliseconds, mutexName = @"Local\CursorTranslator.Update." + lockHash
        });
        using var stream = typeof(UpdateInstallerHandoff).Assembly.GetManifestResourceStream(
            "CursorTranslator.Services.UpdateInstallerHandoff.ps1")!;
        using var reader = new StreamReader(stream);
        var script = "$config = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)) + "')) | ConvertFrom-Json\n"
            + reader.ReadToEnd();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return start;
    }
}
