using System.Text.Json;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Pages.Components;

namespace EndpointSubmission.Service.Endpoints.ENA;

public static class EnaPreviewHandlers
{
    public static async Task<IResult> Render(
        Guid id,
        string? t,
        SubmissionPageAccess access,
        CancellationToken ct)
    {
        var submission = await access.FindAsync(id, t, "ena", ct);
        if (submission is null) return Results.NotFound();

        var payload = JsonSerializer.Deserialize<EnaPackagePayload>(submission.PackagePayloadJson)
            ?? new EnaPackagePayload();

        var releaseDate = submission.ReleaseDate?.ToString("yyyy-MM-dd") ?? "Not selected";
        var checklist = string.IsNullOrWhiteSpace(payload.Checklist) ? "Not specified" : payload.Checklist;

        var content = $@"
          <hgroup>
            <h1>ENA submission preview</h1>
            <p>{Html.Text(submission.ProjectName)}</p>
          </hgroup>
          <dl>
            <dt>DataHUB</dt><dd>{Html.Text(submission.DataHubHost)}</dd>
            <dt>Commit</dt><dd><code>{Html.Text(submission.CommitSha)}</code></dd>
            <dt>Target</dt><dd>{Html.Text(submission.Target.ToString().ToLowerInvariant())}</dd>
            <dt>Checklist</dt><dd>{Html.Text(checklist)}</dd>
            <dt>Release date</dt><dd>{Html.Text(releaseDate)}</dd>
          </dl>
          <p>The RO-Crate snapshot required to prepare this submission was captured during ingestion.</p>";

        return Results.Text(Layout.Render("ENA submission preview", content), "text/html");
    }
}
