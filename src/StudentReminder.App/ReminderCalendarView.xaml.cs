using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StudentReminder.Core;

namespace StudentReminder.App;

public partial class ReminderCalendarView : UserControl
{
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
        if (!loaded) MonthSummary.Text = "";
        RenderSelection();
    }

    internal void RefreshTodayMarker()
    {
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
            var count = days.Where(day => day.IsInMonth).SelectMany(day => day.Entries)
                .Select(entry => entry.Event.Id).Distinct().Count();
            MonthSummary.Text = count == 0 ? "本月暂无安排" : $"本月 {count} 项事项";
        }
        PreviousMonth.IsEnabled = month != DateOnly.MinValue;
        NextMonth.IsEnabled = month.Year != 9999 || month.Month != 12;
        RenderSelection();
        if (focusSelected) FocusSelectedDay();
    }

    private void RenderSelection()
    {
        var dayEntries = entries[selectedDate].ToArray();
        DayTitle.Text = $"{selectedDate:M月d日} {Weekday(selectedDate.DayOfWeek)}";
        DaySummary.Text = loaded ? $"{dayEntries.Length} 条安排" : "";
        Agenda.ItemsSource = dayEntries;
        EmptyDay.Text = loaded ? "这一天没有已确认的安排。" :
            LoadError.Visibility == Visibility.Visible ? "" : "正在读取本地事项…";
        EmptyDay.Visibility = dayEntries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Weekday(DayOfWeek day) => "星期" + "日一二三四五六"[(int)day];

    private void Days_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rendering || Days.SelectedItem is not CalendarDay { Date: { } date } day) return;
        selectedDate = date;
        if (day.IsInMonth) RenderSelection();
        else RenderMonth(focusSelected: true);
    }

    private void Days_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var offset = e.Key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -7,
            Key.Down => 7,
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
    }

    private void Today_Click(object sender, RoutedEventArgs e)
    {
        selectedDate = Today;
        RenderMonth();
    }

    private void AgendaItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: CalendarEntry entry }) OpenEvent?.Invoke(entry.Event.Id);
    }
}
