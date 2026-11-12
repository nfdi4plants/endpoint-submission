using System.Globalization;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using Microsoft.EntityFrameworkCore;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>An entry on WorkflowHub that an earlier commit of the same ARC uploaded an item to.</summary>
/// <param name="EntryId">The WorkflowHub entry id. It is positive.</param>
/// <param name="Version">The version that upload created, or null when the record does not say.</param>
public sealed record WorkflowHubEarlierEntry(int EntryId, int? Version);

/// <summary>
/// Finds the entries that items of a submission were uploaded to from earlier commits. The preview page
/// and the upload both ask here, so what the page offers is what the upload does.
/// </summary>
public static class WorkflowHubEarlierEntries
{
    private const string ObjectKindPrefix = "workflowhub-";

    /// <summary>
    /// The earlier entry per item alias ("{ItemKind}/{ItemIdentifier}"). One query reads the records.
    /// An alias maps to its newest record (newest CreatedAt, then the larger id) that has the object
    /// kind of its item kind, an AccessionNumber that is a positive integer, and a submission that is
    /// another submission of the same DataHUB host, project id, endpoint and target. Records of
    /// <paramref name="submission"/> itself do not count, and neither do records of the other target,
    /// so sandbox and production never mix.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, WorkflowHubEarlierEntry>> FindAsync(
        SubmissionDbContext db, Submission submission, CancellationToken ct)
    {
        var rows = await db.Accessions.AsNoTracking()
            .Where(a => a.SubmissionId != submission.Id
                && a.ObjectKind.StartsWith(ObjectKindPrefix)
                && a.Submission!.Endpoint == "workflowhub"
                && a.Submission.DataHubHost == submission.DataHubHost
                && a.Submission.ProjectId == submission.ProjectId
                && a.Submission.Target == submission.Target)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .Select(a => new { a.Alias, a.ObjectKind, a.AccessionNumber, a.SecondaryAccession })
            .ToListAsync(ct);

        var entries = new Dictionary<string, WorkflowHubEarlierEntry>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            // The rows are newest first, so the first usable row of an alias is the one to keep.
            if (entries.ContainsKey(row.Alias)) continue;

            var slash = row.Alias.IndexOf('/');
            if (slash <= 0 || row.ObjectKind != ObjectKindPrefix + row.Alias[..slash]) continue;
            if (PositiveInt(row.AccessionNumber) is not { } entryId) continue;

            entries[row.Alias] = new WorkflowHubEarlierEntry(entryId, PositiveInt(row.SecondaryAccession));
        }

        return entries;
    }

    private static int? PositiveInt(string? text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : null;
}
