using StudentReminder.Core;

namespace StudentReminder.Notifications;

public interface IReminderNotification
{
    Task SubmitAsync(ReminderBatch batch, CancellationToken ct);
}

public sealed class ReminderDispatcher(IReminderStore store, IReminderNotification notification)
{
    public async Task<bool> DispatchAsync(DateTimeOffset now, bool recovery, bool startup, CancellationToken ct)
    {
        var batch = await store.ClaimRemindersAsync(now, recovery ? now : now.AddMinutes(-1), startup, ct);
        if (batch.Due.Count == 0 && batch.NewlyMissed == 0) return false;
        var submitted = false;
        try
        {
            var due = batch.Due;
            if (due.Count > 0)
            {
                var ids = due.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                due = (await store.ReadClaimedRemindersAsync(ct)).Where(item => ids.Contains(item.Id)).ToArray();
            }
            if (due.Count == 0 && batch.NewlyMissed == 0) return false;
            await notification.SubmitAsync(batch with { Due = due }, ct);
            submitted = true;
        }
        finally
        {
            if (batch.Due.Count != 0)
                await store.FinishRemindersAsync(batch.Due, submitted, CancellationToken.None);
        }
        return true;
    }
}
