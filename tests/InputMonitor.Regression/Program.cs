using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Controls;
using System.Windows.Interop;
using CursorTranslator.Services;
using PixelRect = Avalonia.PixelRect;
using UIA = Interop.UIAutomationClient;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--provider")
        {
            var app = new Application();
            var window = new Window
            {
                Title = "InputMonitor regression provider",
                Width = 240, Height = 100, ShowActivated = false, ShowInTaskbar = false,
                Left = -10000, Top = -10000,
                Content = new ProbeControl(args[1], args[2])
            };
            window.Loaded += (_, _) => Console.WriteLine(new WindowInteropHelper(window).Handle.ToInt64());
            app.Run(window);
            return 0;
        }

        try
        {
            Check("document", false, "", calls =>
                Require(!calls.Contains("DocumentRange"), "A Document must never request its TextPattern range"));
            Check("writable-document", true, "test input", calls =>
                Require(!calls.Contains("DocumentRange"), "A writable Document must use ValuePattern"));
            Check("empty-document", true, "", calls =>
                Require(!calls.Contains("DocumentRange"), "An empty writable Document must clear the input"));
            Check("readonly-value", false, "", calls =>
            {
                Require(!calls.Contains("Value.Get"), "Do not fetch read-only values");
                Require(!calls.Contains("DocumentRange"), "Do not fall through from read-only Value to Text");
            });
            Check("password", false, "", calls =>
                Require(!calls.Contains("Value.Get") && !calls.Contains("DocumentRange"), "Never read passwords"));
            Check("edit-value", true, "test input", calls =>
                Require(!calls.Contains("DocumentRange"), "Working inputs must use their ValuePattern fast path"), editable: true);
            Check("empty-value", true, "", _ => { });
            Check("large-value", false, "", calls =>
                Require(!calls.Contains("DocumentRange"), "Oversized values must not fall through to TextPattern"));
            Check("edit-text", true, "test input", calls =>
            {
                Require(calls.Contains("GetText:100001"), "Text reads must have a finite limit");
                Require(calls.Contains("GetAttributeValue:collapsed"), "Never query attributes across an entire Edit range");
            });
            Check("readonly-text", false, "", calls =>
                Require(!calls.Contains("GetText:"), "Never read read-only TextPattern text"));
            Check("large-text", false, "", calls =>
                Require(calls.Contains("GetText:100001"), "Oversized text must be rejected without an unbounded read"));

            CheckWeak("weak-custom", CaretPlacement.Inside, true, "test input");
            CheckWeak("weak-pane", CaretPlacement.Inside, true, "test input");
            CheckWeak("weak-custom-no-caret", CaretPlacement.None, false);
            CheckWeak("weak-custom-caret-outside", CaretPlacement.Outside, false);
            CheckWeak("weak-custom-readonly", CaretPlacement.Inside, false, checkCalls: calls =>
                Require(!calls.Contains("Value.Get"), "Do not fetch read-only weak values"));
            CheckWeak("weak-custom-password", CaretPlacement.Inside, false, checkCalls: calls =>
                Require(!calls.Contains("Value.Get"), "Never read weak password values"));
            CheckWeak("weak-custom-no-value", CaretPlacement.Inside, false);
            CheckWeak("weak-text", CaretPlacement.Inside, false, checkCalls: calls =>
                Require(!calls.Contains("Value.Get"), "Static text control types are never inputs"));
            CheckWeak("weak-button", CaretPlacement.Inside, false);

            CheckObservation();
            Console.WriteLine("PASS: 21 cross-process UI Automation and observation regression cases");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(string scenario, bool expected, string text, Action<string> checkCalls, bool editable = false)
    {
        var log = Path.GetTempFileName();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("--provider");
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(log);
        using var provider = Process.Start(start)!;
        try
        {
            // Providers must be in another process: InputMonitor deliberately skips itself.
            var ready = provider.StandardOutput.ReadLineAsync();
            Require(ready.Wait(TimeSpan.FromSeconds(15)), "Provider startup timed out");
            var window = AutomationElement.FromHandle(new IntPtr(long.Parse(ready.Result!)));
            var element = window.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "probe"));
            Require(element is not null, "Provider element was not found");
            object?[] parameters = editable
                ? [element, null, false, null, null, null, null]
                : [element, element, provider.Id, null, null, null, null];
            var method = typeof(InputMonitor).GetMethod(editable ? "TryReadEditable" : "TryReadCandidate",
                BindingFlags.NonPublic | BindingFlags.Static)!;
            var success = (bool)method.Invoke(null, parameters)!;
            Require(success == expected, $"{scenario}: expected success={expected}, got {success}");
            if (expected) Require((string)parameters[3]! == text, $"{scenario}: incorrect text");
            checkCalls(File.ReadAllText(log));
            Console.WriteLine($"PASS: {scenario}");
        }
        finally
        {
            if (!provider.HasExited) provider.Kill();
            provider.WaitForExit();
            File.Delete(log);
        }
    }

    private enum CaretPlacement { None, Inside, Outside }

    private static void CheckWeak(string scenario, CaretPlacement caret, bool expected, string text = "",
        Action<string>? checkCalls = null)
    {
        var log = Path.GetTempFileName();
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("--provider");
        start.ArgumentList.Add(scenario);
        start.ArgumentList.Add(log);
        using var provider = Process.Start(start)!;
        try
        {
            var ready = provider.StandardOutput.ReadLineAsync();
            Require(ready.Wait(TimeSpan.FromSeconds(15)), "Provider startup timed out");
            var automation = new UIA.CUIAutomation8();
            var window = automation.ElementFromHandle(new IntPtr(long.Parse(ready.Result!)));
            var element = window.FindFirst(UIA.TreeScope.TreeScope_Descendants,
                automation.CreatePropertyCondition(30011 /* AutomationId */, "probe"));
            Require(element is not null, "Provider element was not found");
            var rect = element!.CurrentBoundingRectangle;
            PixelRect? caretBounds = caret switch
            {
                CaretPlacement.Inside => new PixelRect((rect.left + rect.right) / 2, rect.top + 2, 1, 16),
                CaretPlacement.Outside => new PixelRect(rect.right + 500, rect.bottom + 500, 1, 16),
                _ => null
            };
            File.WriteAllText(log, "");
            object?[] parameters = [element, provider.Id, caretBounds, null, null, null, null];
            var method = typeof(InputMonitor).Assembly
                .GetType("CursorTranslator.Services.WeakFocusedInputSource")!
                .GetMethod("TryReadFocusedInput", BindingFlags.Public | BindingFlags.Static)!;
            var success = (bool)method.Invoke(null, parameters)!;
            Require(success == expected, $"{scenario}: expected success={expected}, got {success}");
            if (expected)
            {
                Require((string)parameters[3]! == text, $"{scenario}: incorrect text");
                Require(((string)parameters[4]!).Contains(":weak:"), $"{scenario}: weak targets need a distinct id");
            }
            checkCalls?.Invoke(File.ReadAllText(log));
            Console.WriteLine($"PASS: {scenario}");
        }
        finally
        {
            if (!provider.HasExited) provider.Kill();
            provider.WaitForExit();
            File.Delete(log);
        }
    }

    private static void CheckObservation()
    {
        using var monitor = new InputMonitor();
        monitor.ConfigureTranslationTriggers(true, false, false, false, 2000);
        var commits = new List<string>();
        var found = 0;
        var lost = 0;
        monitor.TextCommitted += text => commits.Add(text.Text);
        monitor.TargetFound += () => found++;
        monitor.FocusLost += () => lost++;

        const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Instance;
        uint now = 100_000;
        uint lastInput = 0;
        typeof(InputMonitor).GetField("_tickCount", instance)!.SetValue(monitor, new Func<uint>(() => now));
        typeof(InputMonitor).GetField("_lastUserInputTick", instance)!.SetValue(monitor, new Func<uint>(() => lastInput));
        var triggers = typeof(InputMonitor).GetField("_triggerConfiguration", instance)!.GetValue(monitor);
        var process = typeof(InputMonitor).GetMethod("ProcessReadResult", instance)!;
        void Read(string value, string id, bool weak)
            => process.Invoke(monitor, [value, id, new PixelRect(0, 0, 10, 10), "probe", IntPtr.Zero, triggers, weak]);

        Read("hello", "weak-a", true);
        Require(commits.Count == 0 && found == 0, "Existing text in a weak target must not be translated on connect");
        Read("hello", "weak-a", true);
        Require(commits.Count == 0 && found == 0, "An unchanged weak target stays under observation");

        lastInput = now - 200;
        Read("hello w", "weak-a", true);
        Require(found == 1 && commits.SequenceEqual(["hello w"]), "A change that follows input confirms the weak target");

        lastInput = now - 10_000;
        Read("hello wo", "weak-a", true);
        Require(commits.Count == 1, "A change without input must not be translated");
        Require(lost >= 1, "Rejecting a confirmed weak target must release its overlay");
        lastInput = now;
        Read("hello wor", "weak-a", true);
        Require(commits.Count == 1, "A rejected weak target stays rejected");

        lastInput = now - 10_000;
        Read("12:00", "weak-clock", true);
        Read("12:01", "weak-clock", true);
        Require(commits.Count == 1 && found == 1, "Self-updating controls are rejected before confirmation");

        now = 50;
        lastInput = uint.MaxValue - 100;
        Read("", "weak-wrap", true);
        Read("x", "weak-wrap", true);
        Require(commits.Count == 2 && commits[^1] == "x", "Input correlation must survive tick-count wraparound");

        Read("existing", "strong", false);
        Require(commits.Count == 3 && commits[^1] == "existing", "Strong targets keep translating existing text on connect");
        Console.WriteLine("PASS: weak target observation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class ProbeControl(string scenario, string log) : Control
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ProbePeer(this, scenario, log);
}

internal sealed class ProbePeer(ProbeControl owner, string scenario, string log)
    : FrameworkElementAutomationPeer(owner), IValueProvider, ITextProvider
{
    internal void Record(string call) => File.AppendAllText(log, call + "\n");
    protected override string GetAutomationIdCore() => "probe";
    protected override string GetClassNameCore() => "RegressionInput";
    protected override string GetNameCore() => "Regression input";
    protected override bool HasKeyboardFocusCore() => true;
    protected override bool IsPasswordCore() => scenario is "password" or "weak-custom-password";
    protected override AutomationControlType GetAutomationControlTypeCore() => scenario switch
    {
        "weak-pane" => AutomationControlType.Pane,
        "weak-text" => AutomationControlType.Text,
        "weak-button" => AutomationControlType.Button,
        _ when scenario.StartsWith("weak-") => AutomationControlType.Custom,
        _ when scenario.Contains("document") => AutomationControlType.Document,
        _ => AutomationControlType.Edit
    };
    public override object? GetPattern(PatternInterface patternInterface)
    {
        if (scenario.StartsWith("weak-"))
            return patternInterface == PatternInterface.Value && scenario != "weak-custom-no-value" ? this : null;
        if (patternInterface == PatternInterface.Text) return this;
        if (patternInterface == PatternInterface.Value
            && scenario is "edit-value" or "readonly-value" or "writable-document" or "empty-document"
                or "empty-value" or "large-value" or "password") return this;
        return null;
    }
    bool IValueProvider.IsReadOnly => scenario is "readonly-value" or "weak-custom-readonly";
    string IValueProvider.Value
    {
        get
        {
            Record("Value.Get");
            return scenario.StartsWith("empty") ? "" : scenario == "large-value" ? new string('x', 200000) : "test input";
        }
    }
    void IValueProvider.SetValue(string value) => throw new NotSupportedException();
    ITextRangeProvider ITextProvider.DocumentRange
    {
        get
        {
            Record("DocumentRange");
            // Access is a regression, even if the caller catches provider failures.
            if (scenario.Contains("document")) throw new InvalidOperationException("Dangerous whole-document query");
            return new ProbeRange(this, scenario);
        }
    }
    SupportedTextSelection ITextProvider.SupportedTextSelection => SupportedTextSelection.Single;
    ITextRangeProvider[] ITextProvider.GetSelection() => [];
    ITextRangeProvider[] ITextProvider.GetVisibleRanges() => [];
    ITextRangeProvider ITextProvider.RangeFromChild(IRawElementProviderSimple childElement) => throw new NotSupportedException();
    ITextRangeProvider ITextProvider.RangeFromPoint(Point screenLocation) => throw new NotSupportedException();
}

internal sealed class ProbeRange(ProbePeer peer, string scenario) : ITextRangeProvider
{
    private bool _collapsed;
    public object GetAttributeValue(int attributeId)
    {
        peer.Record(_collapsed ? "GetAttributeValue:collapsed" : "GetAttributeValue:whole-range");
        if (!_collapsed) throw new InvalidOperationException("Dangerous whole-range attribute query");
        return scenario == "readonly-text";
    }
    public string GetText(int maxLength)
    {
        peer.Record($"GetText:{maxLength}");
        var value = scenario == "large-text" ? new string('x', 200000) : "test input";
        return maxLength < 0 ? value : value[..Math.Min(maxLength, value.Length)];
    }
    public ITextRangeProvider Clone() => new ProbeRange(peer, scenario);
    public bool Compare(ITextRangeProvider range) => ReferenceEquals(this, range);
    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint) => 0;
    public void ExpandToEnclosingUnit(TextUnit unit) => throw new NotSupportedException();
    public ITextRangeProvider FindAttribute(int attributeId, object value, bool backward) => throw new NotSupportedException();
    public ITextRangeProvider FindText(string text, bool backward, bool ignoreCase) => throw new NotSupportedException();
    public double[] GetBoundingRectangles() => [];
    public IRawElementProviderSimple[] GetChildren() => [];
    public IRawElementProviderSimple GetEnclosingElement() => throw new NotSupportedException();
    public int Move(TextUnit unit, int count) => throw new NotSupportedException();
    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (endpoint != TextPatternRangeEndpoint.End || targetEndpoint != TextPatternRangeEndpoint.Start)
            throw new NotSupportedException();
        _collapsed = true;
    }
    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count) => throw new NotSupportedException();
    public void Select() => throw new NotSupportedException();
    public void AddToSelection() => throw new NotSupportedException();
    public void RemoveFromSelection() => throw new NotSupportedException();
    public void ScrollIntoView(bool alignToTop) => throw new NotSupportedException();
}
