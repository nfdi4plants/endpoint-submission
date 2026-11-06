using System.Globalization;
using System.Text.Json;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Pages.Components;
using Microsoft.EntityFrameworkCore;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

public static class WorkflowHubPreviewHandlers
{
    private sealed record ArtifactRow(
        Guid Id, string Kind, string Identifier, string? Title, PreparedArtifactStatus Status,
        string FailureReasonsJson, string MetadataJson, long? SizeBytes);

    private sealed record EventRow(string Step, SubmissionEventLevel Level, string Message, DateTimeOffset OccurredAt);

    public static async Task<IResult> Render(
        Guid id,
        string? t,
        SubmissionPageAccess access,
        SubmissionDbContext db,
        CancellationToken ct)
    {
        var submission = await access.FindAsync(id, t, "workflowhub", ct);
        if (submission is null) return Results.NotFound();

        // Content stays out of the projection. Crates can be megabytes and the page never shows them.
        var artifacts = await db.PreparedArtifacts.AsNoTracking()
            .Where(a => a.SubmissionId == id)
            .Select(a => new ArtifactRow(
                a.Id, a.ItemKind, a.ItemIdentifier, a.Title, a.Status,
                a.FailureReasonsJson, a.MetadataJson, a.SizeBytes))
            .ToListAsync(ct);

        var events = await db.SubmissionEvents.AsNoTracking()
            .Where(e => e.SubmissionId == id)
            .OrderBy(e => e.OccurredAt).ThenBy(e => e.Id)
            .Select(e => new EventRow(e.Step, e.Level, e.Message, e.OccurredAt))
            .ToListAsync(ct);

        var accessionRows = await db.Accessions.AsNoTracking()
            .Where(a => a.SubmissionId == id)
            .Select(a => new { a.Alias, a.AccessionNumber })
            .ToListAsync(ct);
        var accessions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var accession in accessionRows) accessions.TryAdd(accession.Alias, accession.AccessionNumber);

        var instanceHost = submission.Target == SubmissionTarget.Production ? "workflowhub.eu" : "dev.workflowhub.eu";
        var instanceBase = "https://" + instanceHost;

        var ordered = artifacts
            .OrderBy(a => a.Kind == "workflow" ? 0 : a.Kind == "run" ? 1 : 2)
            .ThenBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Identifier, StringComparer.Ordinal)
            .ToList();

        var rows = ordered.Select(a =>
            accessions.TryGetValue($"{a.Kind}/{a.Identifier}", out var accessionNumber)
                ? UploadedRow(a, instanceBase, accessionNumber)
                : a.Status switch
                {
                    PreparedArtifactStatus.Ready => ReadyRow(a),
                    _ => BlockedRow(a)
                }).ToList();
        var anySelectable = ordered.Any(a =>
            a.Status == PreparedArtifactStatus.Ready && !accessions.ContainsKey($"{a.Kind}/{a.Identifier}"));

        var failedNotice = artifacts.Count == 0 && events.Count > 0 && events[^1].Step == "preparation.failed"
            ? $@"
          <article role=""alert"">
            <p>{Html.Text(events[^1].Message)}</p>
            <p>Click the badge in the DataHUB again to retry.</p>
          </article>"
            : "";

        // A completed preparation without rows means the commit holds no workflows or runs, so the page offers no upload form.
        var nothingToUpload = artifacts.Count == 0 && events.Any(e => e.Step == "preparation.completed");
        var emptyNotice = nothingToUpload
            ? @"
          <article>
            <p>No workflows or runs were found in this ARC commit, so there is nothing to upload.</p>
          </article>"
            : "";

        var license = ordered.Count > 0 ? LicenseLine(ordered[0].MetadataJson) : "";

        var table = rows.Count == 0
            ? ""
            : $@"
            <table>
              <thead><tr><th>Item</th><th>Size</th><th>Visibility</th></tr></thead>
              <tbody>{string.Concat(rows)}
              </tbody>
            </table>";

        var eventRows = string.Concat(events.Select(e => $@"
            <tr>
              <td>{Html.Text(e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))} UTC</td>
              <td>{Html.Text(e.Level.ToString().ToLowerInvariant())}</td>
              <td>{Html.Text(e.Message)}</td>
            </tr>"));

        var form = nothingToUpload
            ? ""
            : $@"
          <form method=""post"" action=""/workflowhub/submissions/{Html.Text(submission.Id.ToString())}/authorize"">
            <input type=""hidden"" name=""t"" value=""{Html.Text(t)}"">{table}
            <p>Uploading requires membership in a WorkflowHub team. You log in on WorkflowHub with your own account and confirm the upload there.</p>
            <p><small>WorkflowHub makes a new entry public for a moment before the service switches it to private.</small></p>
            <button type=""submit""{(anySelectable ? "" : " disabled")}>Log in to WorkflowHub and upload</button>
          </form>";

        var content = $@"
          <hgroup>
            <h1>WorkflowHub submission preview</h1>
            <p>{Html.Text(submission.ProjectName)}</p>
          </hgroup>
          <dl>
            <dt>DataHUB</dt><dd>{Html.Text(submission.DataHubHost)}</dd>
            <dt>Commit</dt><dd><code>{Html.Text(submission.CommitSha)}</code></dd>
            <dt>Target</dt><dd>{Html.Text(submission.Target.ToString().ToLowerInvariant())}</dd>
            <dt>WorkflowHub</dt><dd>{Html.Text(instanceHost)}</dd>
          </dl>{failedNotice}{emptyNotice}{license}{form}
          <h2>Status</h2>
          <table>
            <thead><tr><th>Time</th><th>Level</th><th>Message</th></tr></thead>
            <tbody>{eventRows}
            </tbody>
          </table>";

        return Results.Text(Layout.Render("WorkflowHub submission preview", content), "text/html");
    }

    private static string LicenseLine(string metadataJson)
    {
        var present = true;
        string? spdxId = null;
        try
        {
            using var metadata = JsonDocument.Parse(metadataJson);
            if (metadata.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (metadata.RootElement.TryGetProperty("licensePresent", out var p) && p.ValueKind == JsonValueKind.False)
                    present = false;
                if (metadata.RootElement.TryGetProperty("licenseSpdxId", out var s) && s.ValueKind == JsonValueKind.String)
                    spdxId = s.GetString();
            }
        }
        catch (JsonException)
        {
            // Unreadable metadata falls through to the unrecognized-license wording.
        }

        var text = !present
            ? "The ARC has no LICENSE file, so nothing can be uploaded."
            : !string.IsNullOrWhiteSpace(spdxId)
                ? $"License: {Html.Text(spdxId)}. WorkflowHub shows it on every entry."
                : "The ARC license was not recognized as an SPDX license. Entries arrive without a license, and you can set one on WorkflowHub afterwards.";
        return $"\n          <p>{text}</p>";
    }

    private static string ReadyRow(ArtifactRow a)
    {
        var id = Html.Text(a.Id.ToString());
        return $@"
                <tr>
                  <td><label><input type=""checkbox"" name=""item"" value=""{id}""> {Describe(a)}</label></td>
                  <td>{Size(a.SizeBytes)}</td>
                  <td>
                    <select name=""visibility-{id}"">
                      <option value=""private"" selected>Private</option>
                      <option value=""public"">Public</option>
                    </select>
                  </td>
                </tr>";
    }

    /// <summary>A row for an item that cannot be uploaded, with the reasons beside the disabled checkbox.</summary>
    private static string BlockedRow(ArtifactRow a)
    {
        var reasons = new List<string>();
        try { reasons = JsonSerializer.Deserialize<List<string>>(a.FailureReasonsJson) ?? []; }
        catch (JsonException) { }

        var list = reasons.Count == 0
            ? ""
            : $"<small><ul>{string.Concat(reasons.Select(r => $"<li>{Html.Text(r)}</li>"))}</ul></small>";
        return $@"
                <tr aria-disabled=""true"">
                  <td>
                    <label aria-disabled=""true""><input type=""checkbox"" disabled aria-disabled=""true""> {Describe(a)}</label>
                    {list}
                  </td>
                  <td>{Size(a.SizeBytes)}</td>
                  <td></td>
                </tr>";
    }

    private static string UploadedRow(ArtifactRow a, string instanceBase, string accessionNumber)
    {
        var href = Html.Text($"{instanceBase}/workflows/{Uri.EscapeDataString(accessionNumber)}");
        return $@"
                <tr>
                  <td>{Describe(a)}</td>
                  <td>{Size(a.SizeBytes)}</td>
                  <td>Uploaded: <a href=""{href}"">{Html.Text(accessionNumber)}</a></td>
                </tr>";
    }

    private static string Describe(ArtifactRow a)
    {
        var title = string.IsNullOrWhiteSpace(a.Title) ? "" : $" <small>{Html.Text(a.Title)}</small>";
        return $"<small>{Html.Text(a.Kind)}</small> <strong>{Html.Text(a.Identifier)}</strong>{title}";
    }

    private static string Size(long? bytes)
        => bytes is null ? "" : Html.Text((bytes.Value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)) + " KB";
}
