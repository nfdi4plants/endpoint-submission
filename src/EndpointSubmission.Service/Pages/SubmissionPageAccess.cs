using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;
using Microsoft.EntityFrameworkCore;

namespace EndpointSubmission.Service.Pages;

/// <summary>
/// Shared access check for endpoint-owned submission pages. Rendering remains endpoint-specific;
/// token lookup and comparison must not be reimplemented by every endpoint.
/// </summary>
public class SubmissionPageAccess(SubmissionDbContext db, StatusTokenService tokens)
{
    public async Task<Submission?> FindAsync(
        Guid id,
        string? token,
        string endpoint,
        CancellationToken ct)
    {
        var submission = await db.Submissions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.Endpoint == endpoint, ct);

        return submission is not null && tokens.Verify(token, submission.StatusTokenHash)
            ? submission
            : null;
    }
}
