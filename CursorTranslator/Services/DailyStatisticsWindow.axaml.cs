using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CursorTranslator.Models;
using TextBox = Avalonia.Controls.TextBox;
using KeyEventArgs = Avalonia.Input.KeyEventArgs;

namespace CursorTranslator.Services;

public partial class DailyStatisticsWindow : Window
{
    private readonly UsageStatisticsStore _statisticsStore;
    private DateOnly _startDate;
    private DateOnly _endDate;
    private bool _dateInputError;
    private bool _isQuerying;

    public DailyStatisticsWindow() : this(new UsageStatisticsStore()) { }

    public DailyStatisticsWindow(UsageStatisticsStore statisticsStore)
    {
        InitializeComponent();
        _statisticsStore = statisticsStore;

        var today = DateOnly.FromDateTime(DateTime.Today);
        _startDate = today.AddDays(-29);
        _endDate = today;
        RefreshDateCards();
        Opened += async (_, _) => await QueryStatisticsAsync();
    }

    private async void Query_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await QueryStatisticsAsync();

    private async Task QueryStatisticsAsync()
    {
        if (_isQuerying) return;
        if (StartDateInputBox.IsVisible)
            CommitDateEdit(StartDateInputBox);
        if (EndDateInputBox.IsVisible)
            CommitDateEdit(EndDateInputBox);
        if (_dateInputError)
        {
            ShowQueryError("请先修正无效日期，再查询统计。");
            return;
        }

        var start = _startDate;
        var end = _endDate;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var oldestDate = today.AddDays(-(UsageStatisticsStore.RetentionDays - 1));
        var dayCount = end.DayNumber - start.DayNumber + 1;
        if (start > end)
        {
            ShowQueryError("开始日期不能晚于结束日期。");
            return;
        }
        if (dayCount > 30)
        {
            ShowQueryError("一次最多查询连续30天。请选择最近365天内的日期。 ");
            return;
        }
        if (start < oldestDate || end > today)
        {
            ShowQueryError($"可查询范围为 {oldestDate:yyyy/M/d} 至 {today:yyyy/M/d}。");
            return;
        }

        _isQuerying = true;
        RangeStatusText.Text = "正在读取统计…";
        try
        {
            var stored = await _statisticsStore.GetDailyStatisticsAsync(start, end);
            var storedByDate = stored.ToDictionary(item => item.Date);
            var daily = Enumerable.Range(0, dayCount)
                .Select(offset =>
                {
                    var date = start.AddDays(offset);
                    return storedByDate.TryGetValue(date, out var item)
                        ? item
                        : new DailyTranslationStatistic(date, 0, 0);
                })
                .ToArray();

            StatisticsChart.Statistics = daily;
            RequestTotalText.Text = daily.Sum(item => item.TranslationRequests).ToString("N0", CultureInfo.CurrentCulture);
            UnitsTotalText.Text = daily.Sum(item => item.TranslationUnits).ToString("N0", CultureInfo.CurrentCulture);
            ChartRangeText.Text = $"{start:yyyy/M/d} 至 {end:yyyy/M/d} · {dayCount} 天";
            RangeStatusText.Text = $"{start:yyyy/M/d} 至 {end:yyyy/M/d} · 查询完成";
            RangeStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#81766D"));
        }
        catch (Exception ex)
        {
            AppLog.Error("Statistics window", "Failed to query daily usage statistics.", ex);
            ShowQueryError($"读取统计失败：{ex.Message}");
        }
        finally
        {
            _isQuerying = false;
        }
    }

    private void ShowQueryError(string message)
    {
        RangeStatusText.Text = message;
        RangeStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#A95032"));
    }

    private void StartDateCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!StartDateInputBox.IsVisible)
            BeginDateEdit(StartDateInputBox);
        e.Handled = true;
    }

    private void EndDateCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (!EndDateInputBox.IsVisible)
            BeginDateEdit(EndDateInputBox);
        e.Handled = true;
    }

    private void BeginDateEdit(TextBox input)
    {
        var isStartDate = ReferenceEquals(input, StartDateInputBox);
        var date = isStartDate ? _startDate : _endDate;
        input.Text = FormatDate(date);
        input.IsVisible = true;
        if (isStartDate)
            StartDateCardText.IsVisible = false;
        else
            EndDateCardText.IsVisible = false;

        input.Focus();
        input.CaretIndex = input.Text.Length;
        input.SelectAll();
    }

    private void DateInput_LostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CommitDateEdit(sender as TextBox);

    private void DateInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox input) return;

        if (e.Key == Key.Enter)
        {
            CommitDateEdit(input);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            SetDateEditVisible(input, editing: false);
            _dateInputError = false;
            RangeStatusText.Text = "日期未修改，点击日期卡片可编辑";
            RangeStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#81766D"));
            e.Handled = true;
        }
    }

    private void CommitDateEdit(TextBox? input)
    {
        if (input is null || !input.IsVisible) return;

        if (!TryParseDate(input.Text, out var date))
        {
            SetDateEditVisible(input, editing: false);
            _dateInputError = true;
            ShowQueryError("日期无效，请输入 YYYY/M/D，例如 2026/10/3。");
            return;
        }

        if (ReferenceEquals(input, StartDateInputBox))
            _startDate = date;
        else
            _endDate = date;

        SetDateEditVisible(input, editing: false);
        _dateInputError = false;
        RangeStatusText.Text = "日期已修改，点击查询更新统计";
        RangeStatusText.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#81766D"));
    }

    private void SetDateEditVisible(TextBox input, bool editing)
    {
        var isStartDate = ReferenceEquals(input, StartDateInputBox);
        input.IsVisible = editing;
        if (isStartDate)
        {
            StartDateCardText.Text = FormatDate(_startDate);
            StartDateCardText.IsVisible = !editing;
        }
        else
        {
            EndDateCardText.Text = FormatDate(_endDate);
            EndDateCardText.IsVisible = !editing;
        }
    }

    private void RefreshDateCards()
    {
        StartDateCardText.Text = FormatDate(_startDate);
        EndDateCardText.Text = FormatDate(_endDate);
    }

    private static string FormatDate(DateOnly date) => date.ToString("yyyy/M/d", CultureInfo.InvariantCulture);

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        var separator = text.Contains('/') ? '/' : text.Contains('-') ? '-' : '\0';
        if (separator == '\0' || text.Contains(separator == '/' ? '-' : '/')) return false;
        var parts = text.Split(separator);
        if (parts.Length != 3 || parts[0].Length != 4
            || parts[1].Length is < 1 or > 2 || parts[2].Length is < 1 or > 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var day))
            return false;

        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private void WindowSurface_PointerPressed(object? sender, PointerPressedEventArgs e)
        => WindowChrome.BeginMoveDrag(this, e);

    private void Minimize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeIcon.IsVisible = !maximized;
        RestoreIcon.IsVisible = maximized;
        Avalonia.Controls.ToolTip.SetTip(MaximizeButton, maximized ? "还原" : "最大化");
    }

    private void Close_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();
}
