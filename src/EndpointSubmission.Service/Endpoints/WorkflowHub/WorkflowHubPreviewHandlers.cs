using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Pages.Components;
using EndpointSubmission.Service.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>What the upload form shows again after a refused selection or a login that needs one more answer.</summary>
/// <param name="CheckedItems">Artifact ids that stay ticked.</param>
/// <param name="PublicItems">Artifact ids whose visibility stays on "public".</param>
/// <param name="Teams">The user's teams, known after the WorkflowHub login. With several, the form shows a required radio group.</param>
/// <param name="TeamId">The team chosen in an earlier round. Without a radio group, the form carries it in a hidden field.</param>
public sealed record WorkflowHubFormState(
    IReadOnlySet<Guid> CheckedItems,
    IReadOnlyDictionary<Guid, bool> PublicItems,
    IReadOnlyList<WorkflowHubTeam>? Teams,
    int? TeamId = null);

/// <summary>
/// How many items an upload uploaded, failed, left unknown, partly uploaded or skipped. The upload
/// redirect carries these numbers in the query string and the preview turns them into a banner.
/// The banner is built from the numbers only, so no text from the query string reaches the page.
/// </summary>
public sealed record WorkflowHubUploadCounts(int Uploaded, int Failed, int Unknown, int Partial, int Skipped)
{
    public bool IsEmpty => Uploaded == 0 && Failed == 0 && Unknown == 0 && Partial == 0 && Skipped == 0;

    public static WorkflowHubUploadCounts FromResult(WorkflowHubUploadResult result)
        => new(result.Succeeded, result.Failed, result.Unknown, result.PartiallyFailed, result.Skipped);

    /// <summary>Reads the query values. Anything that is not a non-negative integer counts as zero. Returns null when every count is zero.</summary>
    public static WorkflowHubUploadCounts? Parse(
        string? uploaded, string? failed, string? unknown, string? partial, string? skipped)
    {
        var counts = new WorkflowHubUploadCounts(
            Count(uploaded), Count(failed), Count(unknown), Count(partial), Count(skipped));
        return counts.IsEmpty ? null : counts;
    }

    /// <summary>The counts as query parameters, starting with "&amp;".</summary>
    public string ToQuery()
        => $"&uploaded={Uploaded}&failed={Failed}&unknown={Unknown}&partial={Partial}&skipped={Skipped}";

    private static int Count(string? raw)
        => int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0;
}

public static class WorkflowHubPreviewHandlers
{
    private const string PageTitle = "Submit to WorkflowHub";
    private const string ProductionOffText = "Uploading to the production WorkflowHub is not enabled on this deployment.";
    private const string LoginNotConfiguredText = "WorkflowHub login is not configured on this deployment.";

    // The reason texts come from the ARC and mark code with backticks. The match runs on escaped text.
    private static readonly Regex Backticks = new("`([^`]+)`", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    private sealed record ArtifactRow(
        Guid Id, string Kind, string Identifier, string? Title, PreparedArtifactStatus Status,
        string FailureReasonsJson, string MetadataJson, long? SizeBytes);

    private sealed record EventRow(string Step, SubmissionEventLevel Level, string Message, DateTimeOffset OccurredAt);

    private sealed record AttemptRow(Guid Id, int AttemptNumber, SubmissionStatus Status, string? ErrorSummary);

    // The upload service writes a payload of this kind for every attempt. Its JSON has the artifactId.
    private const string CratePayloadKind = "workflowhub-crate";

    /// <summary>
    /// The upload counts arrive as plain strings, so a malformed value never turns into a 400 response.
    /// Without <paramref name="submissionOptions"/> the page does not restrict uploads. The upload route
    /// checks the production switch itself. The page lists the ready items read-only when the target
    /// is production and the switch is off, and when the target has no OAuth client id or the public
    /// base URL is empty. Each of those checks runs only when its options are given.
    /// </summary>
    public static async Task<IResult> Render(
        Guid id,
        string? t,
        SubmissionPageAccess access,
        SubmissionDbContext db,
        CancellationToken ct,
        IOptions<WorkflowHubOptions>? options = null,
        IOptions<EndpointSubmissionOptions>? submissionOptions = null,
        string? uploaded = null,
        string? failed = null,
        string? unknown = null,
        string? partial = null,
        string? skipped = null)
    {
        var submission = await access.FindAsync(id, t, "workflowhub", ct);
        if (submission is null) return Results.NotFound();

        // FindAsync only returns a submission for a matching token, so t is set here.
        var counts = WorkflowHubUploadCounts.Parse(uploaded, failed, unknown, partial, skipped);
        var productionOff = submission.Target == SubmissionTarget.Production
            && submissionOptions is not null
            && !submissionOptions.Value.AllowProductionTarget;
        var instance = options?.Value is { } workflowHub
            ? (submission.Target == SubmissionTarget.Production ? workflowHub.Production : workflowHub.Sandbox)
            : null;
        var loginMissing = (instance is not null && string.IsNullOrWhiteSpace(instance.ClientId))
            || (submissionOptions is not null && submissionOptions.Value.PublicBaseUrl.Trim().TrimEnd('/').Length == 0);
        var page = await RenderViewAsync(
            db, submission, t!, InstanceBase(submission.Target, options?.Value), "", ct,
            result: counts, uploadAllowed: !productionOff && !loginMissing,
            uploadOffNotice: productionOff ? null : LoginNotConfiguredText);
        return Results.Text(page, "text/html");
    }

    /// <summary>The base URL of the WorkflowHub instance a submission targets, without a trailing slash.</summary>
    public static string InstanceBase(SubmissionTarget target, WorkflowHubOptions? options)
    {
        options ??= new WorkflowHubOptions();
        return (target == SubmissionTarget.Production ? options.Production : options.Sandbox).BaseUrl.TrimEnd('/');
    }

    /// <summary>A notice box for <see cref="RenderViewAsync"/>. The text is escaped here.</summary>
    /// <param name="kind">The style of the box. Unknown kinds fall back to the error style, see <see cref="WorkflowHubResultPages.NoticeKind"/>.</param>
    public static string Notice(string text, string kind = "error") => $@"
          <div class=""wfh-notice wfh-notice-{WorkflowHubResultPages.NoticeKind(kind)}"" role=""alert"">
            <p>{Html.Text(text)}</p>
          </div>";

    /// <summary>
    /// Renders the preview from the stored rows, with the upload form for the items that can still
    /// be uploaded. The status token goes into the form's hidden field.
    /// </summary>
    /// <param name="notice">Markup placed under the header. Callers pass literal or escaped HTML.</param>
    /// <param name="formState">Selections to show again in the form. Null shows the form as first opened.</param>
    /// <param name="result">The counts of the upload that just finished. Null shows no result banner.</param>
    /// <param name="uploadAllowed">False lists the ready items read-only, with a notice and without a form.</param>
    /// <param name="uploadOffNotice">The warning text for <paramref name="uploadAllowed"/> false. Null gives the production-off text.</param>
    public static async Task<string> RenderViewAsync(
        SubmissionDbContext db,
        Submission submission,
        string statusToken,
        string instanceBase,
        string notice,
        CancellationToken ct,
        WorkflowHubFormState? formState = null,
        WorkflowHubUploadCounts? result = null,
        bool uploadAllowed = true,
        string? uploadOffNotice = null)
    {
        var id = submission.Id;

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

        var attemptsByArtifact = await LoadAttemptsByArtifactAsync(db, id, ct);

        var instanceHost = Uri.TryCreate(instanceBase, UriKind.Absolute, out var instanceUri)
            ? instanceUri.Authority
            : instanceBase;

        var ordered = artifacts
            .OrderBy(a => a.Kind == "workflow" ? 0 : a.Kind == "run" ? 1 : 2)
            .ThenBy(a => a.Kind, StringComparer.Ordinal)
            .ThenBy(a => a.Identifier, StringComparer.Ordinal)
            .ToList();

        var uploadedRows = new List<string>();
        var readyRows = new List<string>();
        var blockedRows = new List<string>();
        foreach (var a in ordered)
        {
            var attempts = attemptsByArtifact.GetValueOrDefault(a.Id) ?? [];
            if (accessions.TryGetValue($"{a.Kind}/{a.Identifier}", out var accessionNumber))
                uploadedRows.Add(UploadedRow(a, instanceBase, accessionNumber, VisibilityNotSet(attempts)));
            else if (a.Status == PreparedArtifactStatus.Ready)
                readyRows.Add(ReadyRow(a, formState, instanceBase, uploadAllowed, MayExistOnWorkflowHub(attempts)));
            else
                blockedRows.Add(BlockedItem(a));
        }

        // Without upload rights the ready items are listed but nothing can be selected.
        var anySelectable = readyRows.Count > 0 && uploadAllowed;

        var failedNotice = artifacts.Count == 0 && events.Count > 0 && events[^1].Step == "preparation.failed"
            ? $@"
          <div class=""wfh-notice wfh-notice-error"" role=""alert"">
            <p>{Html.Text(events[^1].Message)}</p>
            <p>Click the badge in the DataHUB again to retry.</p>
          </div>"
            : "";

        // A completed preparation without rows means the commit holds no workflows or runs, so the page offers no upload form.
        var nothingToUpload = artifacts.Count == 0 && events.Any(e => e.Step == "preparation.completed");
        var emptyNotice = nothingToUpload
            ? @"
          <div class=""wfh-notice wfh-notice-info"">
            <p>No workflows or runs were found in this ARC commit, so there is nothing to upload.</p>
          </div>"
            : "";

        var uploadedSection = uploadedRows.Count == 0
            ? ""
            : $@"
          <section class=""wfh-section"" aria-labelledby=""wfh-uploaded-heading"">
            <h2 id=""wfh-uploaded-heading"">Already on WorkflowHub {CountBadge(uploadedRows.Count)}</h2>
            <ul class=""wfh-list"">{string.Concat(uploadedRows)}
            </ul>
          </section>";

        var selectAll = anySelectable
            ? @"
            <label class=""wfh-select-all"" hidden>
              <input type=""checkbox"" data-wfh-select-all> Select all
            </label>"
            : "";

        var readySection = readyRows.Count == 0
            ? ""
            : $@"
          <section class=""wfh-section"" aria-labelledby=""wfh-ready-heading"">
            <h2 id=""wfh-ready-heading"">Ready to upload {CountBadge(readyRows.Count)}</h2>{selectAll}
            <ul class=""wfh-list"">{string.Concat(readyRows)}
            </ul>
          </section>";

        var blockedSection = blockedRows.Count == 0
            ? ""
            : $@"
          <section class=""wfh-section"" aria-labelledby=""wfh-blocked-heading"">
            <h2 id=""wfh-blocked-heading"">Not ready yet {CountBadge(blockedRows.Count)}</h2>
            <p class=""wfh-muted"">These items need changes in the ARC before they can be uploaded.</p>
            <div class=""wfh-list"">{string.Concat(blockedRows)}
            </div>
          </section>";

        string body;
        if (anySelectable)
        {
            body = $@"{uploadedSection}
          <form method=""post"" action=""/workflowhub/submissions/{Html.Text(submission.Id.ToString())}/authorize"" data-wfh-upload>
            <input type=""hidden"" name=""t"" value=""{Html.Text(statusToken)}"">{readySection}{blockedSection}{UploadPanel(formState)}
          </form>";
        }
        else
        {
            body = $"{uploadedSection}{readySection}{blockedSection}";
        }

        var uploadOffHtml = uploadAllowed ? "" : Notice(uploadOffNotice ?? ProductionOffText, "warning");

        var content = $@"
          <hgroup>
            <h1>{PageTitle}</h1>
            <p>{Html.Text(submission.ProjectName)}</p>
          </hgroup>{notice}{ResultBanner(result)}{Summary(submission, instanceHost, ordered.Count > 0 ? ordered[0].MetadataJson : null)}{failedNotice}{emptyNotice}{uploadOffHtml}{body}{ActivitySection(events, OpenActivity(result))}";

        return WorkflowHubResultPages.Shell(PageTitle, content, withScript: anySelectable);
    }

    private static string CountBadge(int count) => $@"<span class=""wfh-count"">{count.ToString(CultureInfo.InvariantCulture)}</span>";

    private static string Plural(int count, string singular, string plural)
        => $"{count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural)}";

    /// <summary>The notice for the upload that just finished. It is built from the counts only.</summary>
    private static string ResultBanner(WorkflowHubUploadCounts? c)
    {
        if (c is null) return "";

        var lines = new List<string>();
        if (c.Uploaded > 0) lines.Add($"{Plural(c.Uploaded, "item", "items")} uploaded to WorkflowHub.");
        if (c.Partial > 0)
            lines.Add($"{Plural(c.Partial, "item was", "items were")} created on WorkflowHub, but setting the visibility failed. {(c.Partial == 1 ? "The entry may be public." : "The entries may be public.")} Check the visibility on WorkflowHub.");
        if (c.Unknown > 0)
            lines.Add($"{Plural(c.Unknown, "item has", "items have")} an unknown result. The entry may exist on WorkflowHub and may be public. Check before uploading again.");
        if (c.Failed > 0)
            lines.Add($"{Plural(c.Failed, "item", "items")} could not be uploaded. The activity log below says what happened and whether an entry may exist on WorkflowHub.");
        if (c.Skipped > 0)
            lines.Add($"{Plural(c.Skipped, "item was", "items were")} skipped because {(c.Skipped == 1 ? "it is" : "they are")} already on WorkflowHub.");

        var kind = c.Failed > 0 && c.Uploaded == 0 && c.Partial == 0 && c.Unknown == 0 ? "error"
            : c.Partial > 0 || c.Unknown > 0 || c.Failed > 0 ? "warning"
            : c.Uploaded > 0 ? "success"
            : "info";

        return $@"
          <div class=""wfh-notice wfh-notice-{kind}"" role=""status"" data-wfh-result>{string.Concat(lines.Select(line => $@"
            <p>{line}</p>"))}
          </div>";
    }

    /// <summary>The card under the header with the ARC, the DataHUB, the commit, the WorkflowHub instance and the license.</summary>
    private static string Summary(Submission submission, string instanceHost, string? metadataJson)
    {
        var sha = submission.CommitSha;
        var shortSha = sha.Length > 8 ? sha[..8] : sha;
        var environment = submission.Target == SubmissionTarget.Production
            ? @"<span class=""wfh-badge wfh-badge-production"">production</span>"
            : @"<span class=""wfh-badge wfh-badge-test"">test instance</span>";

        var license = metadataJson is null ? "" : LicenseFact(metadataJson);

        return $@"
          <article class=""wfh-summary"">
            <dl class=""wfh-facts"">
              <div class=""wfh-fact""><dt>ARC</dt><dd>{Html.Text(submission.ProjectName)}</dd></div>
              <div class=""wfh-fact""><dt>DataHUB</dt><dd>{Html.Text(submission.DataHubHost)}</dd></div>
              <div class=""wfh-fact""><dt>Commit</dt><dd><code title=""{Html.Text(sha)}"">{Html.Text(shortSha)}</code></dd></div>
              <div class=""wfh-fact""><dt>WorkflowHub</dt><dd>{Html.Text(instanceHost)} {environment}</dd></div>{license}
            </dl>
          </article>";
    }

    private static string LicenseFact(string metadataJson)
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

        if (!present)
        {
            return @"
              <div class=""wfh-fact wfh-fact-wide wfh-fact-warning""><dt>License</dt><dd>The ARC has no LICENSE file, so nothing can be uploaded.</dd></div>";
        }

        if (string.IsNullOrWhiteSpace(spdxId))
        {
            return @"
              <div class=""wfh-fact wfh-fact-wide wfh-fact-warning""><dt>License</dt><dd>The ARC license was not recognized as an SPDX license. Entries arrive without a license, and you can set one on WorkflowHub afterwards.</dd></div>";
        }

        return $@"
              <div class=""wfh-fact wfh-fact-wide""><dt>License</dt><dd><strong>{Html.Text(spdxId)}</strong> <small class=""wfh-muted"">WorkflowHub shows it on every entry.</small></dd></div>";
    }

    /// <summary>The panel with the login explanation, the team choice, the notes and the submit button. It sits inside the form.</summary>
    private static string UploadPanel(WorkflowHubFormState? formState)
    {
        var teamPicker = "";
        if (formState?.Teams is { Count: > 1 } teams)
        {
            var radios = string.Concat(teams.Select(team => $@"
                <label><input type=""radio"" name=""team"" value=""{Html.Text(team.Id.ToString(CultureInfo.InvariantCulture))}"" required> {Html.Text(team.Title)}</label>"));
            teamPicker = $@"
            <fieldset class=""wfh-teams"">
              <legend>WorkflowHub team</legend>{radios}
            </fieldset>";
        }
        else if (formState?.TeamId is { } chosenTeam)
        {
            // A round without a team list, for example a declined login, keeps the team the user chose.
            teamPicker = $@"
            <input type=""hidden"" name=""team"" value=""{Html.Text(chosenTeam.ToString(CultureInfo.InvariantCulture))}"">";
        }

        return $@"
          <article class=""wfh-upload"" aria-labelledby=""wfh-upload-heading"">
            <h2 id=""wfh-upload-heading"">Upload</h2>
            <p>WorkflowHub asks you to log in and to confirm that this service may upload for you. Then it sends you back to this page.</p>{teamPicker}
            <ul class=""wfh-notes wfh-muted"">
              <li>Uploading requires membership in a WorkflowHub team.</li>
              <li>One upload takes at most {WorkflowHubUploadHandlers.MaxItemsPerLogin.ToString(CultureInfo.InvariantCulture)} items.</li>
              <li>Data files outside workflows/ that are stored with Git LFS stay pointer files in the crates, which keeps them under WorkflowHub's 100 MB limit. The full files stay in the ARC on the DataHUB.</li>
              <li>WorkflowHub makes a new entry public for a moment before the service switches it to private.</li>
            </ul>
            <div class=""wfh-actions"">
              <button type=""submit"" data-wfh-submit>Continue to WorkflowHub</button>
              <span class=""wfh-inline-error"" role=""alert"" data-wfh-empty hidden>Select at least one item.</span>
              <span class=""wfh-muted"" role=""status"" aria-live=""polite"" data-wfh-status></span>
            </div>
          </article>";
    }

    /// <summary>The activity log opens by itself when the upload that just finished had a problem.</summary>
    private static bool OpenActivity(WorkflowHubUploadCounts? c)
        => c is not null && (c.Failed > 0 || c.Unknown > 0 || c.Partial > 0);

    private static string ActivitySection(IReadOnlyList<EventRow> events, bool open)
    {
        var items = events.Count == 0
            ? @"
            <p class=""wfh-muted"">No activity has been recorded yet.</p>"
            : $@"
            <ol class=""wfh-events"">{string.Concat(events.Select(e => $@"
              <li class=""wfh-event"">
                <span class=""wfh-event-meta"">
                  <time datetime=""{Html.Text(e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture))}"">{Html.Text(e.OccurredAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))} UTC</time>
                  <span class=""wfh-badge wfh-badge-level-{LevelClass(e.Level)}"">{Html.Text(e.Level.ToString().ToLowerInvariant())}</span>
                </span>
                <span class=""wfh-event-message"">{Html.Text(e.Message)}</span>
              </li>"))}
            </ol>";

        return $@"
          <section class=""wfh-section wfh-activity"">
            <details{(open ? " open" : "")}>
              <summary>Activity {CountBadge(events.Count)}</summary>{items}
            </details>
          </section>";
    }

    private static string LevelClass(SubmissionEventLevel level) => level switch
    {
        SubmissionEventLevel.Warning => "warning",
        SubmissionEventLevel.Error => "error",
        _ => "info"
    };

    private static string ReadyRow(
        ArtifactRow a, WorkflowHubFormState? state, string instanceBase, bool uploadAllowed, bool caution)
    {
        var cautionClass = caution ? " wfh-item-caution" : "";

        if (!uploadAllowed)
        {
            var note = caution ? CautionLine(instanceBase) : "";
            return $@"
              <li class=""wfh-item wfh-item-ready wfh-item-readonly{cautionClass}"">
                <div class=""wfh-pick"">{Describe(a)}{note}</div>
                <span class=""wfh-size"">{Size(a.SizeBytes)}</span>
              </li>";
        }

        var id = Html.Text(a.Id.ToString());
        var warning = caution ? CautionLine(instanceBase, $"caution-{id}") : "";
        var isChecked = state?.CheckedItems.Contains(a.Id) == true;
        var isPublic = state?.PublicItems.TryGetValue(a.Id, out var publicChoice) == true && publicChoice;
        return $@"
              <li class=""wfh-item wfh-item-ready{cautionClass}"">
                <div class=""wfh-pick"">
                  <input type=""checkbox"" id=""item-{id}"" name=""item"" value=""{id}""{(caution ? $@" aria-describedby=""caution-{id}"" data-wfh-caution" : "")}{(isChecked ? " checked" : "")}>
                  <label for=""item-{id}"">{Describe(a)}</label>{warning}
                </div>
                <span class=""wfh-size"">{Size(a.SizeBytes)}</span>
                <div class=""wfh-visibility"">
                  <label for=""visibility-{id}"">Visibility</label>
                  <select id=""visibility-{id}"" name=""visibility-{id}"">
                    <option value=""private""{(isPublic ? "" : " selected")}>Private</option>
                    <option value=""public""{(isPublic ? " selected" : "")}>Public</option>
                  </select>
                </div>
              </li>";
    }

    /// <summary>The warning under an item that an earlier upload may have put on WorkflowHub already.</summary>
    /// <param name="paragraphId">The id of the paragraph, so the item's checkbox can point at it. Callers escape it. Null gives the paragraph no id.</param>
    private static string CautionLine(string instanceBase, string? paragraphId = null) => $@"
                  <p class=""wfh-item-warning""{(paragraphId is null ? "" : $@" id=""{paragraphId}""")}>An earlier upload of this item got no clear answer from WorkflowHub. An entry may already exist and may be public. Check WorkflowHub before uploading it again. <a href=""{Html.Text(instanceBase + "/workflows")}"" target=""_blank"" rel=""noopener noreferrer"">Check on WorkflowHub</a></p>";

    /// <summary>An item that cannot be uploaded: closed details with the reasons inside.</summary>
    private static string BlockedItem(ArtifactRow a)
    {
        var reasons = new List<string>();
        try { reasons = JsonSerializer.Deserialize<List<string>>(a.FailureReasonsJson) ?? []; }
        catch (JsonException) { }

        var hint = a.Status == PreparedArtifactStatus.PreparationFailed ? "could not be packed"
            : reasons.Count == 0 ? "needs changes in the ARC"
            : $"{Plural(reasons.Count, "thing", "things")} to fix in the ARC";

        var list = reasons.Count == 0
            ? ""
            : $@"
                <ul>{string.Concat(reasons.Select(r => $"<li>{Reason(r)}</li>"))}</ul>";
        var identifier = HasTitle(a)
            ? $@"
                <p class=""wfh-id"">{Html.Text(a.Identifier)}</p>"
            : "";

        return $@"
              <details class=""wfh-item wfh-item-blocked"">
                <summary>{KindBadge(a.Kind)} <strong>{Html.Text(ItemName(a))}</strong> <span class=""wfh-muted"">&middot; {hint}</span></summary>{identifier}{list}
              </details>";
    }

    /// <summary>Escapes a reason, then turns text between backticks into code. Only the escaped text is touched.</summary>
    private static string Reason(string reason) => Backticks.Replace(Html.Text(reason), "<code>$1</code>");

    private static string UploadedRow(ArtifactRow a, string instanceBase, string accessionNumber, bool visibilityNotSet)
    {
        var href = Html.Text($"{instanceBase}/workflows/{Uri.EscapeDataString(accessionNumber)}");
        var warning = visibilityNotSet
            ? @"
                  <p class=""wfh-item-warning""><span class=""wfh-badge wfh-badge-warning"">Visibility not set</span> Setting the visibility failed. The entry may be public. Check it on WorkflowHub.</p>"
            : "";
        return $@"
              <li class=""wfh-item wfh-item-uploaded"">
                <div class=""wfh-pick"">{Describe(a)}{warning}</div>
                <span class=""wfh-entry"">Entry {Html.Text(accessionNumber)}</span>
                <a href=""{href}"" target=""_blank"" rel=""noopener noreferrer"">Open <span class=""wfh-sr-only"">{Html.Text(ItemName(a))} </span>on WorkflowHub</a>
              </li>";
    }

    /// <summary>
    /// The attempts of this submission per artifact. The link is the artifactId in the description of
    /// the crate request that the upload service stores with every attempt. The page reads that
    /// description and the attempt columns that decide the warnings, and never a receipt or a crate.
    /// </summary>
    private static async Task<Dictionary<Guid, List<AttemptRow>>> LoadAttemptsByArtifactAsync(
        SubmissionDbContext db, Guid submissionId, CancellationToken ct)
    {
        var byArtifact = new Dictionary<Guid, List<AttemptRow>>();

        var attempts = await db.SubmissionAttempts.AsNoTracking()
            .Where(a => a.SubmissionId == submissionId)
            .Select(a => new AttemptRow(a.Id, a.AttemptNumber, a.Status, a.ErrorSummary))
            .ToListAsync(ct);
        if (attempts.Count == 0) return byArtifact;

        var descriptions = await db.SubmissionPayloads.AsNoTracking()
            .Where(p => p.ObjectKind == CratePayloadKind && p.Attempt!.SubmissionId == submissionId)
            .Select(p => new { p.AttemptId, p.Content })
            .ToListAsync(ct);

        var attemptsById = attempts.ToDictionary(a => a.Id);
        foreach (var description in descriptions)
        {
            if (!attemptsById.TryGetValue(description.AttemptId, out var attempt)) continue;
            if (ArtifactIdOf(description.Content) is not { } artifactId) continue;

            if (!byArtifact.TryGetValue(artifactId, out var list)) byArtifact[artifactId] = list = [];
            list.Add(attempt);
        }

        return byArtifact;
    }

    private static Guid? ArtifactIdOf(string requestDescription)
    {
        try
        {
            using var document = JsonDocument.Parse(requestDescription);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("artifactId", out var property)
                && property.ValueKind == JsonValueKind.String
                && property.TryGetGuid(out var artifactId)
                    ? artifactId
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether an item without an accession may be on WorkflowHub anyway. That holds when an attempt has
    /// no recorded answer, because it ended with an unknown outcome or still says Submitting (an upload
    /// that never finished, for example after a restart during the POST). It also holds when WorkflowHub
    /// created the entry and the visibility step failed.
    /// </summary>
    private static bool MayExistOnWorkflowHub(IReadOnlyList<AttemptRow> attempts)
        => attempts.Any(a => a.Status is SubmissionStatus.PartiallyFailed or SubmissionStatus.Submitting
            || (a.Status == SubmissionStatus.Failed
                && a.ErrorSummary?.StartsWith(WorkflowHubUploadService.UnknownOutcomeLead, StringComparison.Ordinal) == true));

    /// <summary>Whether the newest attempt for an uploaded item created the entry but could not set its visibility.</summary>
    private static bool VisibilityNotSet(IReadOnlyList<AttemptRow> attempts)
        => attempts.MaxBy(a => a.AttemptNumber)?.Status == SubmissionStatus.PartiallyFailed;

    private static bool HasTitle(ArtifactRow a) => !string.IsNullOrWhiteSpace(a.Title);

    private static string ItemName(ArtifactRow a) => HasTitle(a) ? a.Title! : a.Identifier;

    private static string KindBadge(string kind)
    {
        var (label, css) = kind switch
        {
            "workflow" => ("Workflow", "workflow"),
            "run" => ("Run", "run"),
            _ => (kind, "other")
        };
        return $@"<span class=""wfh-badge wfh-badge-{css}"">{Html.Text(label)}</span>";
    }

    /// <summary>The item name with its kind badge in front. A titled item shows its identifier on a second line.</summary>
    private static string Describe(ArtifactRow a)
    {
        var identifier = HasTitle(a) ? $@"<span class=""wfh-id"">{Html.Text(a.Identifier)}</span>" : "";
        return $@"<span class=""wfh-name"">{KindBadge(a.Kind)} <strong>{Html.Text(ItemName(a))}</strong></span>{identifier}";
    }

    /// <summary>The size in B, KB or MB. The unit follows from the rounded value, so a size never reads "1024.0 KB".</summary>
    private static string Size(long? bytes)
    {
        if (bytes is not { } value) return "";
        if (value < 1024) return Html.Text($"{value.ToString(CultureInfo.InvariantCulture)} B");

        var kilobytes = Math.Round(value / 1024.0, 1, MidpointRounding.AwayFromZero);
        var text = kilobytes < 1024
            ? $"{kilobytes.ToString("0.0", CultureInfo.InvariantCulture)} KB"
            : $"{(value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB";
        return Html.Text(text);
    }
}
