using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace StudyTracker;

public class TrackerDbFactory : IDesignTimeDbContextFactory<TrackerDb>
{
    public TrackerDb CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<TrackerDb>()
        .UseSqlite("Data Source=App_Data/tracker.db").Options);
}
