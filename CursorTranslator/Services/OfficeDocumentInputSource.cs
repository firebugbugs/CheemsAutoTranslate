using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using System.Windows.Automation;
using CursorTranslator.Models;

namespace CursorTranslator.Services;

/// <summary>
/// Reads the active paragraph from a running Word-compatible document editor when
/// its canvas does not expose text through UI Automation, Win32 Edit, or MSAA.
/// It attaches only to the active foreground document window and keeps no text.
/// </summary>
internal static class OfficeDocumentInputSource
{
    private const uint GaRoot = 2;

    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(
        ref Guid classId,
        IntPtr reserved,
        [MarshalAs(UnmanagedType.Interface)] out object? instance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr window);

    public static bool TryReadFocusedParagraph(
        AutomationElement focusedElement,
        IntPtr nativeFocusHandle,
        out string value,
        out string id,
        out PixelRect bounds,
        out string description)
    {
        value = "";
        id = "";
        bounds = default;
        description = "文档接口未命中";

        if (nativeFocusHandle == IntPtr.Zero)
        {
            description = "文档接口未命中：系统没有原生焦点窗口";
            return false;
        }

        var stage = "读取文档焦点";
        try
        {
            var focusInfo = focusedElement.Current;
            if (focusInfo.IsPassword
                || focusInfo.ControlType != ControlType.Document
                    && focusInfo.ControlType != ControlType.Edit
                    && focusInfo.ControlType != ControlType.Group
                    && focusInfo.ControlType != ControlType.Custom
                    && focusInfo.ControlType != ControlType.Pane)
            {
                description = "文档接口未命中：焦点不是可编辑文档区域";
                return false;
            }

            var foregroundWindow = GetForegroundWindow();
            var foregroundRoot = foregroundWindow == IntPtr.Zero
                ? IntPtr.Zero
                : GetAncestor(foregroundWindow, GaRoot);
            if (foregroundRoot == IntPtr.Zero)
            {
                description = "文档接口未命中：没有前台窗口";
                return false;
            }

            GetWindowThreadProcessId(nativeFocusHandle, out var focusedProcessId);
            if (focusedProcessId == 0 || focusedProcessId != (uint)focusInfo.ProcessId)
            {
                description = "文档接口未命中：UI Automation 焦点和原生焦点不一致";
                return false;
            }

            var applicationType = Type.GetTypeFromProgID("Word.Application", throwOnError: false);
            if (applicationType is null)
            {
                description = "文档接口未命中：Word 自动化接口未注册";
                return false;
            }

            var classId = applicationType.GUID;
            stage = "连接 Word 自动化对象";
            var activeObjectHresult = GetActiveObject(ref classId, IntPtr.Zero, out var applicationObject);
            if (activeObjectHresult < 0 || applicationObject is null)
            {
                description = $"文档接口未命中：没有运行中的 Word 兼容文档对象（0x{activeObjectHresult:X8}）";
                return false;
            }

            object? activeWindowObject = null;
            object? documentObject = null;
            object? selectionObject = null;
            object? paragraphsObject = null;
            object? paragraphObject = null;
            object? rangeObject = null;
            try
            {
                dynamic application = applicationObject;
                stage = "读取活动文档窗口";
                activeWindowObject = application.ActiveWindow;
                if (activeWindowObject is null)
                {
                    description = "文档接口未命中：当前文档窗口不可用";
                    return false;
                }

                dynamic activeWindow = activeWindowObject;
                object? activeWindowHwnd = activeWindow.Hwnd;
                var activeWindowHandle = new IntPtr(Convert.ToInt64(activeWindowHwnd));
                if (activeWindowHandle == IntPtr.Zero
                    || GetAncestor(activeWindowHandle, GaRoot) != foregroundRoot)
                {
                    description = "文档接口未命中：前台窗口和文档窗口不匹配";
                    return false;
                }

                if (!IsDescendantOrSelf(nativeFocusHandle, activeWindowHandle))
                {
                    description = "文档接口未命中：键盘焦点不在文档窗格内";
                    return false;
                }

                GetWindowThreadProcessId(activeWindowHandle, out var windowProcessId);
                if (windowProcessId == 0 || windowProcessId != focusedProcessId)
                {
                    description = "文档接口未命中：文档窗口进程和焦点进程不一致";
                    return false;
                }

                documentObject = application.ActiveDocument;
                selectionObject = application.Selection;
                if (documentObject is null || selectionObject is null)
                {
                    description = "文档接口未命中：活动文档或光标选区不可用";
                    return false;
                }

                dynamic document = documentObject;
                dynamic selection = selectionObject;
                stage = "读取段落集合";
                paragraphsObject = selection.Paragraphs;
                if (paragraphsObject is null)
                {
                    description = "文档接口未命中：当前段落不可用";
                    return false;
                }

                dynamic paragraphs = paragraphsObject;
                // Avoid C#'s dynamic indexer binding here. WPS exposes the
                // Word-compatible Paragraphs collection through IDispatch, and
                // its default member can be mis-bound as string.this[int].
                stage = "获取段落集合第 1 项";
                paragraphObject = paragraphs.Item(1);
                if (paragraphObject is null)
                {
                    description = "文档接口未命中：无法定位光标所在段落";
                    return false;
                }

                dynamic paragraph = paragraphObject;
                stage = "获取段落范围";
                rangeObject = paragraph.Range;
                if (rangeObject is null)
                {
                    description = "文档接口未命中：当前段落范围不可用";
                    return false;
                }

                dynamic range = rangeObject;
                stage = "读取段落文本";
                object? rangeText = range.Text;
                value = Convert.ToString(rangeText) ?? "";
                stage = "读取段落起始位置";
                object? rangeStart = range.Start;
                var paragraphStart = Convert.ToInt32(rangeStart);
                stage = "读取文档名称";
                object? documentNameValue = document.Name;
                string documentName = Convert.ToString(documentNameValue) ?? "";
                stage = "生成文档标识";
                var documentKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(documentName)))[..16];

                var rectangle = focusInfo.BoundingRectangle;
                if (rectangle.Width > 0 && rectangle.Height > 0
                    && !double.IsNaN(rectangle.Left) && !double.IsNaN(rectangle.Top)
                    && !double.IsInfinity(rectangle.Left) && !double.IsInfinity(rectangle.Top))
                {
                    bounds = new PixelRect(
                        (int)rectangle.Left,
                        (int)rectangle.Top,
                        Math.Max(1, (int)rectangle.Width),
                        Math.Max(1, (int)rectangle.Height));
                }

                id = $"{focusedProcessId}:word:{activeWindowHandle.ToInt64():X}:{documentKey}:{paragraphStart}";
                description = "Word-compatible document paragraph";
                return true;
            }
            finally
            {
                ReleaseComObject(rangeObject);
                ReleaseComObject(paragraphObject);
                ReleaseComObject(paragraphsObject);
                ReleaseComObject(selectionObject);
                ReleaseComObject(documentObject);
                ReleaseComObject(activeWindowObject);
            }
        }
        catch (COMException ex)
        {
            value = "";
            id = "";
            bounds = default;
            description = $"文档接口调用失败（{stage}，COM 0x{ex.HResult:X8}）";
            return false;
        }
        catch (InvalidCastException)
        {
            value = "";
            id = "";
            bounds = default;
            description = $"文档接口调用失败（{stage}，自动化对象类型不兼容）";
            return false;
        }
        catch (Exception ex)
        {
            value = "";
            id = "";
            bounds = default;
            description = $"文档接口调用失败（{stage}，{ex.GetType().Name}: {ex.Message}）";
            return false;
        }
    }

    private static bool IsDescendantOrSelf(IntPtr window, IntPtr ancestor)
    {
        for (var current = window; current != IntPtr.Zero; current = GetParent(current))
        {
            if (current == ancestor)
                return true;
        }

        return false;
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); }
            catch (InvalidComObjectException) { }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
