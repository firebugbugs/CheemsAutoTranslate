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
            Console.WriteLine("PASS: 11 cross-process UI Automation regression cases");
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
    protected override bool IsPasswordCore() => scenario == "password";
    protected override AutomationControlType GetAutomationControlTypeCore()
        => scenario.Contains("document") ? AutomationControlType.Document : AutomationControlType.Edit;
    public override object? GetPattern(PatternInterface patternInterface)
    {
        if (patternInterface == PatternInterface.Text) return this;
        if (patternInterface == PatternInterface.Value
            && scenario is "edit-value" or "readonly-value" or "writable-document" or "empty-document"
                or "empty-value" or "large-value" or "password") return this;
        return null;
    }
    bool IValueProvider.IsReadOnly => scenario == "readonly-value";
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
