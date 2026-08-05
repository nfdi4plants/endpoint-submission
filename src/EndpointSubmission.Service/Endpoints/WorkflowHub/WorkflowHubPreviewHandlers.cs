using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Pages.Components;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

public static class WorkflowHubPreviewHandlers
{
    public static async Task<IResult> Render(
        Guid id,
        string? t,
        SubmissionPageAccess access,
        CancellationToken ct)
    {
        var submission = await access.FindAsync(id, t, "workflowhub", ct);
        if (submission is null) return Results.NotFound();

        var content = $@"
          <hgroup>
            <h1>WorkflowHub submission preview</h1>
            <p>{Html.Text(submission.ProjectName)}</p>
          </hgroup>
          <dl>
            <dt>DataHUB</dt><dd>{Html.Text(submission.DataHubHost)}</dd>
            <dt>Commit</dt><dd><code>{Html.Text(submission.CommitSha)}</code></dd>
            <dt>Target</dt><dd>{Html.Text(submission.Target.ToString().ToLowerInvariant())}</dd>
          </dl>
          <p>The RO-Crate snapshot required to prepare this submission was captured during ingestion.</p>";

        return Results.Text(Layout.Render("WorkflowHub submission preview", content), "text/html");
    }
}
