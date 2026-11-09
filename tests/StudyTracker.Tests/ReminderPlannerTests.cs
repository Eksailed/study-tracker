using System.Text.Json;
using System.Text.Json.Nodes;
using StudyTracker.Notifications;
using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class ReminderPlannerTests : TelegramFixture
{
    private async Task<JsonElement> Prepare()
    {
        await BindAsync();
        await SettingsAsync();
        return await TaskAsync();
    }

    private async Task<ApiResponse> ChangeTask(JsonElement task, string field, string value)
    {
        var body = JsonNode.Parse(task.GetRawText())!.AsObject();
        body[field] = value;
        return await Client.Put($"assignments/{task.GetProperty("id").GetInt32()}", body);
    }

    private static DateTimeOffset At(ReminderDelivery row) => DateTimeOffset.FromUnixTimeMilliseconds(row.ScheduledAt);

    [Fact]
    public async Task MoscowReminderTimes()
    {
        await Prepare();

        await AdvanceAsync("2026-11-11T16:00:00Z");
        await PlanAsync();
        var before = (await RowsAsync())[0];
        Assert.Equal(ReminderKind.Before, before.Kind);
        Assert.Equal(DeliveryState.Pending, before.State);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T16:00:00Z"), At(before));

        await AdvanceAsync("2026-11-12T06:00:00Z");
        await PlanAsync();
        var due = (await RowsAsync()).Single(r => r.Kind == ReminderKind.Due);
        Assert.Equal(DateTimeOffset.Parse("2026-11-12T06:00:00Z"), At(due));
    }

    [Fact]
    public async Task RestartDoesNotDuplicateReminder()
    {
        await Prepare();

        await AdvanceAsync("2026-11-11T16:00:00Z");
        await PlanAsync();
        await AdvanceAsync("2026-11-11T16:01:00Z");
        await PlanAsync();

        Assert.Single(await RowsAsync());
    }

    [Fact]
    public async Task CompletedAssignmentIsNotNotified()
    {
        var task = await Prepare();
        Assert.Equal(200, (await ChangeTask(task, "status", "Done")).Status);

        await AdvanceAsync("2026-11-11T16:00:00Z");
        await PlanAsync();

        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task ChangedDueDateCancelsOldPendingReminder()
    {
        var task = await Prepare();
        await AdvanceAsync("2026-11-11T16:00:00Z");
        await PlanAsync();

        Assert.Equal(200, (await ChangeTask(task, "dueDate", "2026-11-15")).Status);
        await PlanAsync();

        Assert.Equal(DeliveryState.Cancelled, (await RowsAsync())[0].State);
    }

    [Fact]
    public async Task DisconnectedChatCancelsPendingReminders()
    {
        await Prepare();
        await AdvanceAsync("2026-11-11T16:00:00Z");
        await PlanAsync();

        Assert.Equal(204, (await Client.Delete("notifications/connection")).Status);

        Assert.Equal(DeliveryState.Cancelled, (await RowsAsync())[0].State);
    }

    [Fact]
    public async Task MissedWindowIsSkipped()
    {
        await Prepare();

        await AdvanceAsync("2026-11-11T16:14:59Z");
        await PlanAsync();
        Assert.Equal(DeliveryState.Pending, (await RowsAsync())[0].State);

        await AdvanceAsync("2026-11-11T16:15:00Z");
        await PlanAsync();
        Assert.Equal(DeliveryState.Skipped, (await RowsAsync())[0].State);
    }

    [Fact]
    public async Task ChangedTimeReplansUnsentReminder()
    {
        await Prepare();
        await AdvanceAsync("2026-11-11T16:00:00Z");
        await PlanAsync();

        await SettingsAsync(settings => { settings["beforeTime"] = "19:05"; return settings; });
        await PlanAsync();
        Assert.Equal(DeliveryState.Cancelled, (await RowsAsync())[0].State);

        await AdvanceAsync("2026-11-11T16:05:00Z");
        await PlanAsync();
        var rows = await RowsAsync();
        Assert.Equal(DeliveryState.Pending, rows[0].State);
        Assert.Equal(DateTimeOffset.Parse("2026-11-11T16:05:00Z"), At(rows[0]));
    }
}
