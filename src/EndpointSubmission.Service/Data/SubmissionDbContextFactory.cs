using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EndpointSubmission.Service.Data;

/// <summary>
/// Design-time context used by `dotnet ef`. Kept separate from the running application so
/// that generating a migration never depends on a real connection string being configured —
/// the design-time value below is never connected to.
/// </summary>
public class SubmissionDbContextFactory : IDesignTimeDbContextFactory<SubmissionDbContext>
{
    public SubmissionDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<SubmissionDbContext>()
            .UseNpgsql("Host=localhost;Database=endpoint_submission;Username=postgres;Password=postgres")
            .Options;

        return new SubmissionDbContext(options);
    }
}
