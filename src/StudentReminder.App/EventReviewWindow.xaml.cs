using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using StudentReminder.Core;
using StudentReminder.Data;

namespace StudentReminder.App;

public partial class EventReviewWindow : Window
{
    private const string EditableTimeFormat = "yyyy-MM-dd HH:mm zzz";
    private const string EditableSecondsFormat = "yyyy-MM-dd HH:mm:ss zzz";
    private readonly SqliteMessageRepository repository;
    private readonly CancellationTokenSource lifetime = new();
    private IReadOnlyList<ReviewedEvent> events = [];
    private bool ready;
    private bool busy;
    private bool changingSelection;
    private bool detailsLoaded;
    private int selectionGeneration;
    private EventCandidate? linkDefaults;
    private (string Id, bool Candidate)? navigation;
    internal event Action? EventsChanged;

    public EventReviewWindow(SqliteMessageRepository repository)
    {
        this.repository = repository;
        InitializeComponent();
        CategoryBox.ItemsSource = new[] { "assignment", "exam", "meeting", "notice", "registration", "payment", "activity", "other" }
            .Select(value => new CategoryChoice(value)).ToArray();
        CategoryFilter.ItemsSource = new[] { new CategoryChoice("") }.Concat(CategoryBox.Items.Cast<CategoryChoice>()).ToArray();
        CategoryFilter.SelectedIndex = 0;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (ready) return;
        ready = true;
        if (navigation is null) await RunAsync(() => ReloadAsync());
        else await FollowNavigationAsync();
    }

    private async void View_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ready) await RunAsync(() => ReloadAsync());
    }

    private async void DateFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (ready) await RunAsync(() => ReloadAsync());
    }

    public async Task NavigateAsync(string id, bool isCandidate)
    {
        navigation = (id, isCandidate);
        if (ready && !busy) await FollowNavigationAsync();
    }

    private Task FollowNavigationAsync() => RunAsync(async () =>
    {
        if (navigation is not { } target) return;
        navigation = null;
        DateFilter.SelectedDate = null;
        CategoryFilter.SelectedIndex = 0;
        ViewBox.SelectedIndex = target.Candidate ? 0 : 2;
        await ReloadAsync(target.Id, requireMatch: true);
        if (SelectedRow is null && target.Candidate)
        {
            ViewBox.SelectedIndex = 1;
            await ReloadAsync(target.Id, requireMatch: true);
        }
        if (SelectedRow is null) ReviewStatus.Text = "该事项或候选已删除，或无法读取。";
    });

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RunAsync(() => ReloadAsync(SelectedRow?.Id));

    private async Task ReloadAsync(string? selectId = null, bool requireMatch = false)
    {
        var view = ViewBox.SelectedIndex;
        var loaded = await Task.Run(async () =>
        {
            var current = await repository.ReadEventsAsync(lifetime.Token);
            var candidates = view == 2 ? [] : await repository.ReadCandidatesAsync(view == 1, lifetime.Token);
            return (current, candidates);
        }, lifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        events = loaded.current;
        var rows = (view == 2 ? events.Select(row => new ReviewRow(null, row))
            : loaded.candidates.Select(row => new ReviewRow(row, null))).Where(MatchesFilters).ToArray();
        changingSelection = true;
        ReviewGrid.ItemsSource = rows;
        ReviewGrid.SelectedItem = rows.FirstOrDefault(row => row.Id == selectId) ?? (requireMatch ? null : rows.FirstOrDefault());
        changingSelection = false;
        await ShowSelectedAsync();
        ReviewStatus.Text = rows.Length == 0
            ? (view == 0 ? "暂无待确认事项。AI 提取后可在这里刷新。" : "暂无记录。")
            : $"共 {rows.Length} 项。";
    }

    private bool MatchesFilters(ReviewRow row)
    {
        if (CategoryFilter.SelectedItem is CategoryChoice { Value.Length: > 0 } category && row.Draft.Category != category.Value)
            return false;
        if (DateFilter.SelectedDate is not { } date) return true;
        var day = DateOnly.FromDateTime(date);
        return row.Draft.StartsAt?.ToLocalTime().Date == date.Date || row.Draft.DeadlineAt?.ToLocalTime().Date == date.Date ||
            row.Draft.StartsOn == day || row.Draft.DeadlineOn == day;
    }

    private ReviewRow? SelectedRow => ReviewGrid.SelectedItem as ReviewRow;

    private async void Row_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ready && !changingSelection && !busy) await ShowSelectedAsync();
    }

    private async Task ShowSelectedAsync()
    {
        var generation = ++selectionGeneration;
        detailsLoaded = false;
        DetailPanel.IsEnabled = false;
        ReviewEvidenceBox.Clear();
        VersionBox.ItemsSource = null;
        VersionDetails.Text = "";
        if (SelectedRow is not { } row)
        {
            DetailPanel.Visibility = Visibility.Hidden;
            return;
        }
        DetailPanel.Visibility = Visibility.Visible;
        var candidate = row.Candidate;
        var reviewed = row.Event;
        var pending = candidate?.Status == "pending";
        CandidateActions.Visibility = candidate is null ? Visibility.Collapsed : Visibility.Visible;
        EventActions.Visibility = reviewed is null ? Visibility.Collapsed : Visibility.Visible;
        VersionPanel.Visibility = reviewed is null ? Visibility.Collapsed : Visibility.Visible;
        LinkPanel.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = IgnoreButton.IsEnabled = pending;
        DeleteCandidateButton.IsEnabled = candidate?.Status is "pending" or "ignored";
        EditButton.IsEnabled = reviewed?.Status is "active" or "completed";
        CompleteButton.IsEnabled = reviewed?.Status == "active";
        linkDefaults = null;
        ShowDraft(row.Draft);
        SummaryLabel.Text = reviewed is not null
            ? $"{row.Status} · v{reviewed.Version}"
            : $"{row.Status} · {ReviewLabels.Change(candidate!.ChangeKind)} · {(row.Draft.Certainty == "explicit" ? "明确表述" : "需核对表述")}";
        AmbiguityLabel.Text = string.Join(" · ", row.Draft.Ambiguities.Concat(
            new[] { ("开始原文", row.Draft.StartText), ("截止原文", row.Draft.DeadlineText) }
                .Where(value => value.Item2 is not null).Select(value => $"{value.Item1} {value.Item2}")));
        if (pending)
        {
            var ordered = events.OrderByDescending(item => item.Details.Title == row.Draft.Title)
                .ThenByDescending(item => item.Details.Category == row.Draft.Category);
            LinkBox.ItemsSource = new[] { new LinkChoice(null) }.Concat(ordered.Select(item => new LinkChoice(item))).ToArray();
            LinkBox.SelectedIndex = 0;
            var matches = events.Count(item => item.Details.Title == row.Draft.Title && item.Details.Category == row.Draft.Category);
            ComparisonLabel.Text = matches == 0 ? "确认前可关联已有事项。" : $"找到 {matches} 项同名同类事项，请查看时间和动作后决定是否关联。";
        }
        try
        {
            var details = await Task.Run(async () =>
            {
                var evidence = await repository.ReadReviewEvidenceAsync(row.Id, reviewed is not null, lifetime.Token);
                var versions = reviewed is null ? [] : await repository.ReadEventVersionsAsync(row.Id, lifetime.Token);
                return (evidence, versions);
            }, lifetime.Token);
            if (generation != selectionGeneration || lifetime.IsCancellationRequested) return;
            var affected = details.evidence.Count(item => item.Status != "active");
            EvidenceHeading.Text = affected == 0 ? $"来源证据（{details.evidence.Count}）"
                : $"来源证据（{details.evidence.Count}，其中 {affected} 条已撤回或有冲突）";
            ReviewEvidenceBox.Text = string.Join("\n\n", details.evidence.Select(FormatEvidence));
            if (pending && details.evidence.All(item => item.Status != "active")) ConfirmButton.IsEnabled = false;
            VersionBox.ItemsSource = details.versions.Select(version => new VersionChoice(version)).ToArray();
            if (details.versions.Count > 0) VersionBox.SelectedIndex = 0;
            detailsLoaded = true;
            DetailPanel.IsEnabled = !busy;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation == selectionGeneration) ReviewStatus.Text = "暂时无法读取来源证据或版本。";
        }
    }

    private void Link_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRow?.Candidate is not { Status: "pending" } candidate) return;
        if (LinkBox.SelectedItem is not LinkChoice { Event: { } target })
        {
            ComparisonLabel.Text = "确认后将创建独立事项。";
            ApplyLinkDefaults(candidate.Draft, candidate.Draft);
            return;
        }
        ComparisonLabel.Text = $"当前 v{target.Version} · {DescribeDraft(target.Details)}\n候选 · {DescribeDraft(candidate.Draft)}";
        if (candidate.ChangeKind == "new")
        {
            ComparisonLabel.Text += "\n关联后保留已有事项字段，并合并证据。";
        }
        if (candidate.ChangeKind == "update")
        {
            var draft = candidate.Draft;
            var keepStart = draft.StartsAt is null && draft.StartsOn is null && draft.StartText is null;
            var keepDeadline = draft.DeadlineAt is null && draft.DeadlineOn is null && draft.DeadlineText is null;
            ApplyLinkDefaults(draft, draft with
            {
                Action = draft.Action.Length == 0 ? target.Details.Action : draft.Action,
                Location = draft.Location ?? target.Details.Location,
                StartsAt = keepStart ? target.Details.StartsAt : draft.StartsAt,
                StartsOn = keepStart ? target.Details.StartsOn : draft.StartsOn,
                DeadlineAt = keepDeadline ? target.Details.DeadlineAt : draft.DeadlineAt,
                DeadlineOn = keepDeadline ? target.Details.DeadlineOn : draft.DeadlineOn
            });
        }
    }

    private void ApplyLinkDefaults(EventCandidate original, EventCandidate defaults)
    {
        var previous = linkDefaults ?? original;
        if (ActionBox.Text == previous.Action) ActionBox.Text = defaults.Action;
        if (LocationBox.Text == (previous.Location ?? "")) LocationBox.Text = defaults.Location ?? "";
        if (StartBox.Text == DisplayTime(previous.StartsAt, previous.StartsOn)) StartBox.Text = DisplayTime(defaults.StartsAt, defaults.StartsOn);
        if (DeadlineBox.Text == DisplayTime(previous.DeadlineAt, previous.DeadlineOn)) DeadlineBox.Text = DisplayTime(defaults.DeadlineAt, defaults.DeadlineOn);
        linkDefaults = defaults;
    }

    private void Version_Changed(object sender, SelectionChangedEventArgs e)
    {
        VersionDetails.Text = VersionBox.SelectedItem is VersionChoice { Revision: var revision }
            ? DescribeDraft(revision.Details) + $"\n该版本引用 {revision.Details.Evidence.Count} 条消息。"
            : "";
    }

    private void ShowDraft(EventCandidate draft)
    {
        TitleBox.Text = draft.Title;
        CategoryBox.SelectedItem = CategoryBox.Items.Cast<CategoryChoice>().FirstOrDefault(item => item.Value == draft.Category);
        ActionBox.Text = draft.Action;
        LocationBox.Text = draft.Location ?? "";
        StartBox.Text = DisplayTime(draft.StartsAt, draft.StartsOn);
        DeadlineBox.Text = DisplayTime(draft.DeadlineAt, draft.DeadlineOn);
    }

    private EventCandidate ReadDraft(EventCandidate original)
    {
        var (start, startDate) = ReadTime(StartBox.Text);
        var (deadline, deadlineDate) = ReadTime(DeadlineBox.Text);
        return original with
        {
            Title = TitleBox.Text, Category = (CategoryBox.SelectedItem as CategoryChoice)?.Value ?? "",
            Action = ActionBox.Text, Location = string.IsNullOrWhiteSpace(LocationBox.Text) ? null : LocationBox.Text,
            StartsAt = start, DeadlineAt = deadline, StartsOn = startDate, DeadlineOn = deadlineDate
        };
    }

    private static (DateTimeOffset? Time, DateOnly? Day) ReadTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return (null, date);
        if (DateTimeOffset.TryParseExact(text.Trim(), [EditableTimeFormat, EditableSecondsFormat],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return (time, null);
        throw new ArgumentException("请输入日期，或带时区的具体时间，例如 2026-11-13 18:00 +08:00。");
    }

    private async void Confirm_Click(object sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedRow?.Candidate is not { } candidate) return;
        var target = (LinkBox.SelectedItem as LinkChoice)?.Event;
        var draft = target is not null && candidate.ChangeKind is ("new" or "cancel") ? target.Details : ReadDraft(candidate.Draft);
        var eventId = await Task.Run(() => repository.ConfirmCandidateAsync(candidate.Id, draft, target?.Id, target?.Version, lifetime.Token));
        EventsChanged?.Invoke();
        if (!lifetime.IsCancellationRequested)
        {
            ViewBox.SelectedIndex = 2;
            await ReloadAsync(eventId);
            ReviewStatus.Text = "事项已确认并保存到本机。";
        }
    });

    private async void Ignore_Click(object sender, RoutedEventArgs e) => await SetDispositionAsync(delete: false);
    private async void DeleteCandidate_Click(object sender, RoutedEventArgs e) => await SetDispositionAsync(delete: true);

    private Task SetDispositionAsync(bool delete) => RunAsync(async () =>
    {
        if (SelectedRow?.Candidate is not { } candidate || (delete && !ConfirmDeletion("候选"))) return;
        await Task.Run(() => repository.SetCandidateDispositionAsync(candidate.Id, delete, lifetime.Token));
        await ReloadAsync();
        ReviewStatus.Text = delete ? "候选已删除。" : "候选已忽略。";
    });

    private async void Edit_Click(object sender, RoutedEventArgs e) => await UpdateEventAsync("edit");
    private async void Complete_Click(object sender, RoutedEventArgs e) => await UpdateEventAsync("complete");
    private async void DeleteEvent_Click(object sender, RoutedEventArgs e) => await UpdateEventAsync("delete");

    private Task UpdateEventAsync(string operation) => RunAsync(async () =>
    {
        if (SelectedRow?.Event is not { } item || (operation == "delete" && !ConfirmDeletion("事项"))) return;
        var draft = operation == "edit" ? ReadDraft(item.Details) : null;
        await Task.Run(() => repository.UpdateEventAsync(item.Id, item.Version, operation, draft, lifetime.Token));
        EventsChanged?.Invoke();
        await ReloadAsync(item.Id);
        ReviewStatus.Text = operation switch { "edit" => "已保存新版本。", "complete" => "已标记完成。", _ => "事项已删除。" };
    });

    private bool ConfirmDeletion(string label) => MessageBox.Show(this, $"从列表中删除此{label}？", "删除",
        MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        busy = true;
        ReviewGrid.IsEnabled = ViewBox.IsEnabled = CategoryFilter.IsEnabled = DateFilter.IsEnabled = DetailPanel.IsEnabled = false;
        try { await action(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ArgumentException error) { ReviewStatus.Text = error.Message; }
        catch (InvalidOperationException error) { ReviewStatus.Text = error.Message; }
        catch (Exception) { ReviewStatus.Text = "本地事项操作失败，请刷新后重试。"; }
        finally
        {
            busy = false;
            ReviewGrid.IsEnabled = ViewBox.IsEnabled = CategoryFilter.IsEnabled = DateFilter.IsEnabled = true;
            DetailPanel.IsEnabled = detailsLoaded && SelectedRow is not null;
            if (navigation.HasValue && !lifetime.IsCancellationRequested)
                _ = Dispatcher.BeginInvoke(async () => await FollowNavigationAsync());
        }
    }

    private void Window_Closed(object? sender, EventArgs e) => lifetime.Cancel();

    private static string FormatEvidence(StoredMessage item)
    {
        var status = item.Status switch { "recalled" => "已撤回", "conflict" => "有内容冲突", _ => "有效" };
        return $"{item.Source} / {item.Account} / {item.Conversation} · {item.LocalSentAt:yyyy-MM-dd HH:mm zzz} · {status}\n" +
            $"会话 {item.Message.ConversationId} · 消息 {item.Message.MessageId ?? item.Id}\n{item.Text}";
    }

    private static string DisplayTime(DateTimeOffset? time, DateOnly? date) => time is { } value
        ? value.ToLocalTime().ToString(value.Second == 0 ? EditableTimeFormat : EditableSecondsFormat, CultureInfo.InvariantCulture)
        : date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    private static string DescribeDraft(EventCandidate draft) =>
        $"{draft.Title} · {ReviewLabels.Category(draft.Category)} · 动作 {draft.Action} · 地点 {draft.Location ?? "—"}" +
        $" · 开始 {(DisplayTime(draft.StartsAt, draft.StartsOn) is { Length: > 0 } start ? start : "—")}" +
        $" · 截止 {(DisplayTime(draft.DeadlineAt, draft.DeadlineOn) is { Length: > 0 } deadline ? deadline : "—")}";

    private sealed record ReviewRow(ReviewCandidate? Candidate, ReviewedEvent? Event)
    {
        public string Id => Candidate?.Id ?? Event!.Id;
        public EventCandidate Draft => Candidate?.Draft ?? Event!.Details;
        public string Title => Draft.Title;
        public string Category => ReviewLabels.Category(Draft.Category);
        public string Status => ReviewLabels.Status(Candidate?.Status ?? Event!.Status);
        public StatusGlyph Glyph => (Candidate?.Status ?? Event!.Status) switch
        {
            "pending" => StatusGlyph.PendingReview,
            "completed" => StatusGlyph.Completed,
            _ => StatusGlyph.None
        };
    }

    private sealed record CategoryChoice(string Value)
    {
        public override string ToString() => Value.Length == 0 ? "全部类别" : ReviewLabels.Category(Value);
    }

    private sealed record LinkChoice(ReviewedEvent? Event)
    {
        public override string ToString() => Event?.ToString() ?? "创建独立事项";
    }

    private sealed record VersionChoice(EventRevision Revision)
    {
        public override string ToString() => $"v{Revision.Version} · {ReviewLabels.Status(Revision.Status)} · {Revision.ChangedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
    }
}
