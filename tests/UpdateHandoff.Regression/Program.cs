using System.Diagnostics;
using CursorTranslator.Services;

internal static class Program
{
    private static readonly List<Process> Children = [];
    private static string Executable => Environment.ProcessPath!;

    private static int Main(string[] args)
    {
        if (args.Contains("--hold")) { Thread.Sleep(60_000); return 0; }
        if (args.Contains("--exit-soon")) { Thread.Sleep(1500); return 0; }
        var modePath = Path.Combine(AppContext.BaseDirectory, "mode.txt");
        if (File.Exists(modePath))
        {
            var mode = File.ReadAllText(modePath);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "app-process.txt"), Environment.ProcessId.ToString());
            File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "arguments.txt"), args);
            if (mode == "installer-failure") return 5;
            if (mode == "installer-success")
            {
                var descendantExe = Path.Combine(AppContext.BaseDirectory, "..", "target", Path.GetFileName(Executable));
                using var child = Process.Start(new ProcessStartInfo(descendantExe, "--hold")
                    { UseShellExecute = false, CreateNoWindow = true })!;
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "descendant.txt"), child.Id.ToString());
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launched.txt"), mode);
            return 0;
        }

        var sandbox = Path.Combine(Path.GetTempPath(), "更新 test ' $value 空格 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sandbox);
        try
        {
            Check(sandbox, "hung-parent", failure: false, identityMismatch: false, normalExit: false);
            Check(sandbox, "normal-exit", failure: false, identityMismatch: false, normalExit: true);
            Check(sandbox, "identity-mismatch", failure: false, identityMismatch: true, normalExit: false);
            Check(sandbox, "installer-failure", failure: true, identityMismatch: false, normalExit: false);
            CheckDuplicate(sandbox);
            Console.WriteLine("PASS: 5 isolated update handoff scenarios (Windows PowerShell 5.1)");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var child in Children)
            {
                try { if (!child.HasExited) child.Kill(); child.WaitForExit(5000); }
                catch (ArgumentException) { }
                finally { child.Dispose(); }
            }
            foreach (var marker in Directory.EnumerateFiles(sandbox, "descendant.txt", SearchOption.AllDirectories))
            {
                try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(marker))); child.Kill(); child.WaitForExit(5000); }
                catch (ArgumentException) { }
            }
            foreach (var marker in Directory.EnumerateFiles(sandbox, "app-process.txt", SearchOption.AllDirectories))
            {
                try { using var child = Process.GetProcessById(int.Parse(File.ReadAllText(marker))); child.WaitForExit(5000); }
                catch (ArgumentException) { }
            }
            var expectedPrefix = Path.Combine(Path.GetTempPath(), "更新 test ' $value 空格 ");
            Require(Path.GetFullPath(sandbox).StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase), "Cleanup must stay inside the test sandbox");
            for (var attempt = 0; ; attempt++)
            {
                try { Directory.Delete(sandbox, recursive: true); break; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
                catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(100); }
            }
        }
    }

    private static Process Child(string arguments)
    {
        var child = Process.Start(new ProcessStartInfo(Executable, arguments)
            { UseShellExecute = false, CreateNoWindow = true })!;
        Children.Add(child);
        return child;
    }

    private static string Fixture(string root, string mode)
    {
        var folder = Path.Combine(root, mode);
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { Path.GetFileName(Executable), "UpdateHandoff.Regression.dll",
                     "UpdateHandoff.Regression.runtimeconfig.json", "UpdateHandoff.Regression.deps.json" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), Path.Combine(folder, name));
        File.WriteAllText(Path.Combine(folder, "mode.txt"), mode);
        return Path.Combine(folder, Path.GetFileName(Executable));
    }

    private static UpdateInstallerHandoff.ProcessIdentity Identity(Process child)
        => new(child.Id, child.StartTime.ToUniversalTime().Ticks, Executable);

    private static void Check(string sandbox, string name, bool failure, bool identityMismatch, bool normalExit)
    {
        var folder = Path.Combine(sandbox, name);
        Directory.CreateDirectory(folder);
        var installer = Fixture(folder, failure ? "installer-failure" : "installer-success");
        var target = Fixture(folder, "target");
        var fallback = Fixture(folder, "fallback");
        var partial = Path.Combine(folder, "package.part");
        var metadata = Path.Combine(folder, "download-state.json");
        File.WriteAllText(partial, "partial");
        File.WriteAllText(metadata, "metadata");
        var parent = Child(normalExit ? "--exit-soon" : "--hold");
        var orphan = Child("--hold");
        var identities = new[] { Identity(parent), Identity(orphan) };
        if (identityMismatch)
            identities = [identities[0] with { StartTimeUtcTicks = 1 }, identities[1] with { Path = target }];
        var eventName = @"Local\Cheems.UpdateTest." + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var helper = Process.Start(UpdateInstallerHandoff.CreateStartInfo(installer, target, fallback,
            partial, metadata, Path.Combine(folder, "handoff.log"), identities, eventName, normalExit ? 3500 : 500))!;
        Require(ready.WaitOne(5000), name + ": helper did not become ready");
        Require(helper.WaitForExit(12_000), name + ": helper did not exit (possibly waiting for installer descendant)");
        Require(helper.ExitCode == (failure ? 1 : 0), name + ": unexpected helper exit code; " + File.ReadAllText(Path.Combine(folder, "handoff.log")));
        var marker = Path.Combine(Path.GetDirectoryName(failure ? fallback : target)!, "launched.txt");
        Require(SpinWait.SpinUntil(() => File.Exists(marker), 5000), name + ": application was not restarted");
        Require(File.Exists(installer) == failure && File.Exists(partial) == failure && File.Exists(metadata) == failure,
            name + ": failed updates must retain packages; successful updates must clean them");
        Require(parent.HasExited != identityMismatch && orphan.HasExited != identityMismatch,
            name + ": only matching process identities may be terminated");
        var log = File.ReadAllText(Path.Combine(folder, "handoff.log"));
        if (normalExit) Require(!log.Contains($"terminating verified application process {parent.Id}."), "Normally exiting parent was force killed");
        var arguments = File.ReadAllLines(Path.Combine(Path.GetDirectoryName(installer)!, "arguments.txt"));
        Require(arguments.Any(arg => arg.StartsWith("/LOG=") && arg.Contains("update-setup.log")), "Installer detail log missing");
        Require(arguments.Contains("/NORESTARTAPPLICATIONS"), "Installer must not restart processes behind the helper");
        Console.WriteLine("PASS: " + name);
    }

    private static void CheckDuplicate(string sandbox)
    {
        var folder = Path.Combine(sandbox, "duplicate");
        Directory.CreateDirectory(folder);
        var installer = Fixture(folder, "installer-failure");
        var target = Fixture(folder, "target");
        var fallback = Fixture(folder, "fallback");
        var parent = Child("--hold");
        var firstName = @"Local\Cheems.UpdateTest." + Guid.NewGuid().ToString("N");
        var secondName = firstName + ".second";
        using var firstReady = new EventWaitHandle(false, EventResetMode.ManualReset, firstName);
        using var secondReady = new EventWaitHandle(false, EventResetMode.ManualReset, secondName);
        ProcessStartInfo Start(string ready, string log) => UpdateInstallerHandoff.CreateStartInfo(installer,
            target, fallback, Path.Combine(folder, "partial"), Path.Combine(folder, "metadata"),
            Path.Combine(folder, log), [Identity(parent)], ready, 5000);
        var first = Process.Start(Start(firstName, "first.log"))!;
        Children.Add(first);
        Require(firstReady.WaitOne(5000), "First helper did not become ready");
        using var second = Process.Start(Start(secondName, "second.log"))!;
        Require(second.WaitForExit(5000) && second.ExitCode == 1 && !secondReady.WaitOne(0), "Second helper must reject concurrent installation");
        Require(!File.Exists(Path.Combine(Path.GetDirectoryName(fallback)!, "launched.txt")), "Rejected helper must not restart fallback");
        Require(first.WaitForExit(12_000), "First helper failed to finish");
        Console.WriteLine("PASS: duplicate helper rejected");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
