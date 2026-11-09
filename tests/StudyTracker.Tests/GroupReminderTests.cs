using StudyTracker.Tests.Support;

namespace StudyTracker.Tests;

public class GroupReminderTests : TelegramFixture
{
    [Fact]
    public async Task GroupTaskIsNotRemindedInTelegram()
    {
        await BindAsync();
        await SettingsAsync();
        var personal = (await TaskAsync()).GetProperty("id").GetInt32();
        var group = GroupSupport.CreateGroup(App, "ИВТ-4");
        GroupSupport.Join(App, group, "telegram@example.test");
        var (_, shared) = GroupSupport.AddGroupTask(App, group);
        App.Scope(services =>
        {
            var db = services.GetRequiredService<TrackerDb>();
            db.Assignments.Single(a => a.Id == shared).DueDate = new DateOnly(2026, 11, 12);
            db.SaveChanges();
            return 0;
        });

        await AdvanceAsync("2026-11-11T16:00:00Z");
        await PlanAsync();

        var row = Assert.Single(await RowsAsync());
        Assert.Equal(personal, row.AssignmentId);
    }
}
