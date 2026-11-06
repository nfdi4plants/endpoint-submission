using EndpointSubmission.Service.Pages.Components;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// The page shell of the WorkflowHub pages and the pages for upload outcomes that have no stored
/// rows to render. The shell adds the WorkflowHub stylesheet to the shared layout.
/// </summary>
public static class WorkflowHubResultPages
{
    private const string StylesheetHead = @"<link rel=""stylesheet"" href=""/css/workflowhub.css"" />";
    private const string ScriptHead = @"<script src=""/js/workflowhub.js"" defer></script>";

    /// <summary>Wraps WorkflowHub page content in the shared layout with the WorkflowHub assets.</summary>
    /// <param name="content">Markup. Callers escape every value that came from outside.</param>
    /// <param name="withScript">Adds the script that gives feedback on the upload form.</param>
    internal static string Shell(string title, string content, bool withScript = false)
        => Layout.Render(
            title,
            $@"<div class=""wfh"">{content}
      </div>",
            withScript ? StylesheetHead + "\n    " + ScriptHead : StylesheetHead);

    /// <summary>A page with one headline and a message card. Unknown kinds fall back to the error style, see <see cref="NoticeKind"/>.</summary>
    /// <param name="bodyHtml">Markup for the card body. Callers escape every value that came from outside.</param>
    internal static string MessagePage(string title, string bodyHtml, string kind = "error") => Shell(title, $@"
      <article class=""wfh-message wfh-message-{NoticeKind(kind)}"">
        <h1>{Html.Text(title)}</h1>{bodyHtml}
      </article>");

    /// <summary>Returns "info" or "warning" as given and maps every other value to "error", so a notice always gets a style class.</summary>
    internal static string NoticeKind(string kind) => kind is "info" or "warning" ? kind : "error";

    /// <summary>The page for a WorkflowHub account without a team, which cannot upload.</summary>
    /// <param name="instanceBase">The WorkflowHub instance's base URL, for the link to its teams.</param>
    /// <param name="previewPath">The path of the submission page with its status token, for the link back. Null shows no link.</param>
    public static string NoTeam(string instanceBase, string? previewPath = null)
    {
        var back = string.IsNullOrEmpty(previewPath)
            ? ""
            : $@"
          <a href=""{Html.Text(previewPath)}"" role=""button"" class=""outline"">Back to the submission</a>";

        return MessagePage("Nothing was uploaded", $@"
        <p>Uploading to WorkflowHub requires membership in a WorkflowHub team, and your WorkflowHub account is not a member of any team. Join an existing team or request a new one on WorkflowHub, then open your submission from the badge link and upload again.</p>
        <div class=""wfh-actions"">
          <a href=""{Html.Text(instanceBase.TrimEnd('/') + "/projects")}"" target=""_blank"" rel=""noopener noreferrer"" role=""button"">WorkflowHub teams</a>{back}
        </div>", "warning");
    }
}
