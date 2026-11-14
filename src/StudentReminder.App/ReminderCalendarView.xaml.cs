using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudentReminder.Core;

namespace StudentReminder.App;

public partial class ReminderCalendarView : UserControl
{
    public static readonly DependencyProperty DayColumnCountProperty = DependencyProperty.Register(
        nameof(DayColumnCount), typeof(int), typeof(ReminderCalendarView), new PropertyMetadata(7));

    public int DayColumnCount
    {
        get => (int)GetValue(DayColumnCountProperty);
        private set => SetValue(DayColumnCountProperty, value);
    }

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Today);
    private DateOnly selectedDate = Today;
    private DateOnly displayedToday = Today;
    private ILookup<DateOnly, CalendarEntry> entries = Array.Empty<CalendarEntry>().ToLookup(entry => entry.Date);
    private bool rendering;
    private bool loaded;

    internal event Action<string>? OpenEvent;

    public ReminderCalendarView()
    {
        InitializeComponent();
        RefreshLocalTime();
        RenderMonth();
    }

    internal void SetEvents(IReadOnlyList<ReviewedEvent> events)
    {
        entries = ReminderCalendar.Index(events, TimeZoneInfo.Local);
        loaded = true;
        LoadError.Visibility = Visibility.Collapsed;
        RenderMonth();
    }

    internal void ShowLoadError()
    {
        LoadError.Visibility = Visibility.Visible;
        if (!loaded) MonthSummary.Text = "暂时无法读取安排";
        RenderSelection();
    }

    internal void RefreshLocalTime()
    {
        var now = DateTime.Now;
        var message = now.Hour switch
        {
            < 6 => "夜深了，记得给自己一点休息时间。",
            < 11 => "早上好，按自己的节奏开启今天。",
            < 14 => "中午好，忙碌之余也记得好好吃饭。",
            < 18 => "下午好，慢慢来，一件一件做就好。",
            _ => "晚上好，今天辛苦了，记得给自己留点时间。"
        };
        Greeting.Text = message;
        GreetingDate.Text = $"{now:M月d日} · {Weekday(now.DayOfWeek)}";
        if (displayedToday != Today) RenderMonth();
    }

    private void RenderMonth(bool focusSelected = false)
    {
        focusSelected |= Days.IsKeyboardFocusWithin;
        displayedToday = Today;
        var month = new DateOnly(selectedDate.Year, selectedDate.Month, 1);
        var days = ReminderCalendar.BuildMonth(month, displayedToday, entries);
        rendering = true;
        Days.ItemsSource = days;
        Days.SelectedItem = days.Single(day => day.Date == selectedDate);
        rendering = false;
        MonthTitle.Text = $"{month:yyyy 年 M 月}";
        if (loaded)
        {
            var monthEntries = days.Where(day => day.IsInMonth).SelectMany(day => day.Entries).ToArray();
            var started = monthEntries.Count(entry => !entry.IsCompleted && !entry.IsDeadline);
            var deadline = monthEntries.Count(entry => !entry.IsCompleted && entry.IsDeadline);
            var completed = monthEntries.Where(entry => entry.IsCompleted).Select(entry => entry.Event.Id).Distinct().Count();
            MonthSummary.Text = monthEntries.Length == 0 ? "本月暂无安排" :
                $"本月  开始 {started} 项 · 截止 {deadline} 项 · 已完成 {completed} 项";
        }
        PreviousMonth.IsEnabled = month != DateOnly.MinValue;
        NextMonth.IsEnabled = month.Year != 9999 || month.Month != 12;
        RenderSelection();
        if (Days.SelectedItem is { } selected) Days.ScrollIntoView(selected);
        if (focusSelected) FocusSelectedDay();
    }

    private void RenderSelection()
    {
        var dayEntries = entries[selectedDate].ToArray();
        DayTitle.Text = $"{selectedDate:M月d日} {Weekday(selectedDate.DayOfWeek)}";
        DaySummary.Text = loaded && dayEntries.Length > 0 ? $"{dayEntries.Length} 条安排" : "";
        DaySummary.Visibility = DaySummary.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Agenda.ItemsSource = dayEntries;
        EmptyDay.Text = loaded ? "这一天还没有安排。" :
            LoadError.Visibility == Visibility.Visible ? "暂时无法读取安排。" : "正在读取本地事项…";
        EmptyState.Visibility = dayEntries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        AgendaScroll.Visibility = dayEntries.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Weekday(DayOfWeek day) => "星期" + "日一二三四五六"[(int)day];

    private void Days_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || e.NewSize.Width <= 0) return;

        var columns = e.NewSize.Width switch
        {
            >= 770 => 7,
            >= 660 => 5,
            _ => 4
        };
        if (DayColumnCount == columns) return;

        DayColumnCount = columns;
        WeekdayHeadings.Visibility = columns == 7 ? Visibility.Visible : Visibility.Collapsed;
        if (Days.SelectedItem is { } selected) Days.ScrollIntoView(selected);
    }

    private void Days_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || Days.SelectedItem is not CalendarDay { Date: { } date } day) return;
        selectedDate = date;
        if (day.IsInMonth) RenderSelection();
        else RenderMonth(focusSelected: true);
        AgendaScroll.ScrollToTop();
    }

    private void Day_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: CalendarDay { Date: not null } day }) return;
        e.Handled = true;
        Days.SelectedItem = day;
        FocusSelectedDay();
    }

    private void Days_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            if (AgendaScroll.IsVisible) AgendaScroll.Focus();
            return;
        }

        var offset = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -DayColumnCount,
            Key.Down => DayColumnCount,
            _ => 0
        };
        if (offset == 0) return;

        e.Handled = true;
        var number = selectedDate.DayNumber + offset;
        if (number < 0 || number > DateOnly.MaxValue.DayNumber) return;

        var date = DateOnly.FromDayNumber(number);
        if (date.Month != selectedDate.Month || date.Year != selectedDate.Year)
        {
            selectedDate = date;
            RenderMonth(focusSelected: true);
            AgendaScroll.ScrollToTop();
            return;
        }

        Days.SelectedItem = Days.Items.Cast<CalendarDay>().Single(day => day.Date == date);
        FocusSelectedDay();
    }

    private void FocusSelectedDay()
    {
        Days.UpdateLayout();
        if (Days.ItemContainerGenerator.ContainerFromItem(Days.SelectedItem) is ListBoxItem item) item.Focus();
    }

    private void PreviousMonth_Click(object sender, RoutedEventArgs e) => MoveMonth(-1);
    private void NextMonth_Click(object sender, RoutedEventArgs e) => MoveMonth(1);

    private void MoveMonth(int offset)
    {
        var target = new DateOnly(selectedDate.Year, selectedDate.Month, 1).AddMonths(offset);
        selectedDate = target.Year == Today.Year && target.Month == Today.Month ? Today : target;
        RenderMonth();
        AgendaScroll.ScrollToTop();
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        selectedDate = Today;
        RenderMonth();
        AgendaScroll.ScrollToTop();
    }

    private void AgendaItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalendarEntry entry }) OpenEvent?.Invoke(entry.Event.Id);
    }
}
