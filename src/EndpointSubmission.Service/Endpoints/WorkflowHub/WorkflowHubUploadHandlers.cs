using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Pages.Components;
using EndpointSubmission.Service.Services;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// The upload of the WorkflowHub preview page. The user ticks items, logs in on WorkflowHub and
/// confirms there, and the service uploads with the OAuth access token of that login. The service
/// is a public OAuth client (authorization code with PKCE S256), so it holds no client secret.
///
/// The access token exists in one place only: a local variable of <see cref="Callback"/>. That one
/// request exchanges the authorization code for the token, then lists the user's teams and uploads.
/// A finally block revokes the token. Neither the token nor the code is stored or logged, and no page
/// shows them. The PKCE verifier and the state sit in the encrypted login cookie
/// (see <see cref="WorkflowHubFlowCookie"/>) and reach no log line and no page.
///
/// The secret status token <c>t</c> protects the authorize route. The callback has no form, so it
/// takes the status token from the encrypted login cookie that the authorize route wrote.
/// </summary>
public static class WorkflowHubUploadHandlers
{
    private const string CallbackPath = "/workflowhub/oauth/callback";
    private const string InvalidLoginText =
        "This WorkflowHub login is no longer valid. Open your submission from the badge link and start again.";
    private const string SeveralTeamsText =
        "You are a member of several WorkflowHub teams. Choose the team to upload to, then continue. WorkflowHub asks you to confirm once more.";

    /// <summary>
    /// The most items one login takes. The login cookie grows with every ticked item, and a browser
    /// drops a cookie above about 4096 bytes.
    /// </summary>
    internal const int MaxItemsPerLogin = 25;

    // The error codes of RFC 6749 section 4.1.2.1. Only these reach the log, so the query string
    // of a forged callback cannot write its own text there.
    private static readonly HashSet<string> AuthorizationErrorCodes = new(StringComparer.Ordinal)
    {
        "invalid_request", "unauthorized_client", "access_denied", "unsupported_response_type",
        "invalid_scope", "server_error", "temporarily_unavailable"
    };

    /// <param name="PublicBase">The public origin of this service without a trailing slash.</param>
    private sealed record OAuthTarget(string BaseUrl, string ClientId, string RedirectUri, string PublicBase);

    /// <summary>
    /// Checks the selection, remembers it in the login cookie and sends the user to WorkflowHub to
    /// log in and confirm.
    /// </summary>
    public static async Task<IResult> Authorize(
        Guid id,
        HttpContext http,
        SubmissionPageAccess access,
        SubmissionDbContext db,
        WorkflowHubUploadService uploads,
        WorkflowHubFlowCookie cookies,
        IOptions<EndpointSubmissionOptions> endpointOptions,
        IOptions<WorkflowHubOptions> options,
        CancellationToken ct)
    {
        var form = await ReadForm(http.Request, ct);
        var statusToken = form["t"].ToString();

        var submission = await access.FindAsync(id, statusToken, "workflowhub", ct);
        if (submission is null) return Results.NotFound();

        var previewPath = PreviewPath(submission.Id, statusToken);
        var (target, failure) = Resolve(submission, previewPath, endpointOptions.Value, options.Value);
        if (target is null) return failure!;

        Task<IResult> Redisplay(string message) => RedisplayAsync(
            db, submission, statusToken, target.BaseUrl, 400, "error", message, ReadFormState(form), ct);

        var (items, problem) = ReadSelection(form);
        if (items is null) return await Redisplay(problem!);

        if (items.Count > MaxItemsPerLogin)
        {
            return await Redisplay(
                $"One upload takes at most {MaxItemsPerLogin} items. Select fewer and upload the rest afterwards.");
        }

        problem = await uploads.ValidateSelection(submission.Id, items, ct);
        if (problem is not null) return await Redisplay(problem);

        int? teamId = null;
        if (form.TryGetValue("team", out var chosen))
        {
            if (!int.TryParse(chosen.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed <= 0)
                return await Redisplay("The chosen WorkflowHub team is not valid.");
            teamId = parsed;
        }

        var state = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        cookies.Write(http, new WorkflowHubPendingFlow(
            submission.Id,
            statusToken,
            items.Select(item => new WorkflowHubSelectedItem(item.ArtifactId, item.MakePublic)).ToList(),
            teamId,
            state,
            verifier,
            cookies.NewExpiry()));

        return SeeOther(http.Response,
            WfHubClient.WorkflowHubApi.authorizeUrl(target.BaseUrl, target.ClientId, target.RedirectUri, state, challenge));
    }

    /// <summary>
    /// Where WorkflowHub sends the browser back to, with a code or an error. A login works once:
    /// once the state matches, the cookie is deleted in this response, whatever happens next. A
    /// callback with another state leaves the cookie alone, so a forged or stale request cannot end
    /// the login in progress.
    /// </summary>
    public static async Task<IResult> Callback(
        string? code,
        string? state,
        string? error,
        HttpContext http,
        SubmissionPageAccess access,
        SubmissionDbContext db,
        WorkflowHubUploadService uploads,
        IWorkflowHubGateway gateway,
        WorkflowHubFlowCookie cookies,
        KeyedAsyncLock locks,
        IOptions<EndpointSubmissionOptions> endpointOptions,
        IOptions<WorkflowHubOptions> options,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(WorkflowHubUploadHandlers));

        var flow = cookies.Read(http.Request);
        if (flow is null)
        {
            cookies.Delete(http);
            return InvalidLoginPage();
        }

        if (!SameValue(flow.State, state) || (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(error)))
            return InvalidLoginPage();

        cookies.Delete(http);

        var submission = await access.FindAsync(flow.SubmissionId, flow.StatusToken, "workflowhub", ct);
        if (submission is null) return Results.NotFound();

        var previewPath = PreviewPath(submission.Id, flow.StatusToken);
        var (target, failure) = Resolve(submission, previewPath, endpointOptions.Value, options.Value);
        if (target is null) return failure!;

        Task<IResult> Redisplay(
            int status, string kind, string message, IReadOnlyList<WorkflowHubTeam>? teams = null) => RedisplayAsync(
            db, submission, flow.StatusToken, target.BaseUrl, status, kind, message, FormStateOf(flow, teams), ct);

        if (!string.IsNullOrEmpty(error))
        {
            if (error == "access_denied")
            {
                return await Redisplay(400, "warning",
                    "You did not confirm the upload on WorkflowHub. Nothing was uploaded.");
            }

            logger.LogWarning("WorkflowHub refused the login for submission {SubmissionId} with error {Error}",
                submission.Id, AuthorizationErrorCodes.Contains(error) ? error : "other");
            return await Redisplay(400, "error", "WorkflowHub refused the login. Nothing was uploaded.");
        }

        string accessToken;
        try
        {
            // The exchange ignores the request token. Once WorkflowHub has issued an access token, the
            // request must reach the finally block below that revokes it, and a closed tab would
            // abandon the wait and leave the token alive. The client limits each call to 100 seconds.
            accessToken = await gateway.ExchangeCodeAsync(
                target.BaseUrl, target.ClientId, code!, target.RedirectUri, flow.CodeVerifier, CancellationToken.None);
        }
        catch (WorkflowHubGatewayException ex)
        {
            logger.LogWarning(
                "The WorkflowHub code exchange failed for submission {SubmissionId} with {Kind} and status {Status}",
                submission.Id, ex.Kind, ex.Status);
            return Page(502, "WorkflowHub login failed", WithBackButton(
                "<p>WorkflowHub could not complete the login. Nothing was uploaded.</p>", previewPath));
        }

        try
        {
            IReadOnlyList<WorkflowHubTeam> teams;
            try
            {
                teams = await uploads.ListTeamsAsync(submission.Id, accessToken, ct);
            }
            // SEEK answers /people/current with 404 "No user logged in" for a token it does not know.
            catch (WorkflowHubGatewayException ex) when (ex.Kind == WorkflowHubFailureKind.Rejected && ex.Status is 401 or 403 or 404)
            {
                return await Redisplay(400, "error", "WorkflowHub did not accept the login. Start the upload again.");
            }
            catch (WorkflowHubGatewayException ex)
            {
                logger.LogWarning("Listing WorkflowHub teams failed for submission {SubmissionId} with {Kind}",
                    submission.Id, ex.Kind);
                return Page(502, "WorkflowHub unavailable", WithBackButton(
                    "<p>WorkflowHub could not be reached or answered with an error. Nothing was uploaded.</p>",
                    previewPath));
            }

            if (teams.Count == 0)
                return Results.Text(WorkflowHubResultPages.NoTeam(target.BaseUrl, previewPath), "text/html");

            int teamId;
            if (flow.TeamId is { } chosen)
            {
                if (teams.All(team => team.Id != chosen))
                {
                    return await Redisplay(400, "error",
                        "You are not a member of the chosen WorkflowHub team. Choose another one.", teams);
                }

                teamId = chosen;
            }
            else if (teams.Count == 1)
            {
                teamId = teams[0].Id;
            }
            else
            {
                return await Redisplay(200, "info", SeveralTeamsText, teams);
            }

            var selected = flow.Items.Select(item => (item.ArtifactId, item.MakePublic)).ToList();

            // Two callbacks of one submission can arrive together, from two logins that run at the same
            // time in different tabs or browsers, or from a repeated consent on WorkflowHub. The second
            // waits here, and its checks then find the accessions of the first, so no crate goes to
            // WorkflowHub twice.
            WorkflowHubUploadResult result;
            using (await locks.AcquireAsync("upload|" + submission.Id.ToString("D"), ct))
            {
                result = await uploads.UploadAsync(submission.Id, selected, teamId, accessToken, ct);
            }

            if (result.Problem is not null) return await Redisplay(400, "error", result.Problem);

            // The counts let the preview show what this upload did. They hold no text, only numbers.
            return SeeOther(http.Response,
                target.PublicBase + previewPath + WorkflowHubUploadCounts.FromResult(result).ToQuery());
        }
        finally
        {
            await RevokeQuietly(gateway, target, accessToken, logger);
        }
    }

    /// <summary>
    /// Ends the access token. The response does not depend on it, so a failure is logged by exception
    /// type and HTTP status only, because a message could quote request details.
    /// </summary>
    private static async Task RevokeQuietly(
        IWorkflowHubGateway gateway, OAuthTarget target, string accessToken, ILogger logger)
    {
        // The request may be cancelled by now, for example by a closed tab. The revoke still has to
        // run, so it has a limit of its own.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await gateway.RevokeAsync(target.BaseUrl, target.ClientId, accessToken, limit.Token);
        }
        catch (WorkflowHubGatewayException ex)
        {
            logger.LogWarning("Revoking the WorkflowHub access token failed with {ExceptionType} and status {Status}",
                ex.GetType().Name, ex.Status);
        }
        catch (Exception ex)
        {
            logger.LogWarning("Revoking the WorkflowHub access token failed with {ExceptionType}", ex.GetType().Name);
        }
    }

    /// <summary>The instance a submission uploads to, or the page that explains why it cannot.</summary>
    private static (OAuthTarget? Target, IResult? Failure) Resolve(
        Submission submission, string previewPath, EndpointSubmissionOptions endpoint, WorkflowHubOptions options)
    {
        if (submission.Target == SubmissionTarget.Production && !endpoint.AllowProductionTarget)
        {
            return (null, Page(403, "Production upload is off", WithBackButton(
                "<p>Uploading to the production WorkflowHub is not enabled on this deployment.</p>", previewPath), "warning"));
        }

        var instance = submission.Target == SubmissionTarget.Production ? options.Production : options.Sandbox;
        var publicBase = endpoint.PublicBaseUrl.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(instance.ClientId) || publicBase.Length == 0)
        {
            return (null, Page(503, "WorkflowHub login unavailable", WithBackButton(
                "<p>WorkflowHub login is not configured on this deployment.</p>", previewPath)));
        }

        return (new OAuthTarget(
            WorkflowHubPreviewHandlers.InstanceBase(submission.Target, options),
            instance.ClientId,
            publicBase + CallbackPath,
            publicBase), null);
    }

    /// <summary>The ticked items with their visibility, or the reason the form cannot be used.</summary>
    private static (List<(Guid ArtifactId, bool MakePublic)>? Items, string? Problem) ReadSelection(IFormCollection form)
    {
        var items = new List<(Guid, bool)>();
        foreach (var raw in form["item"])
        {
            if (!Guid.TryParse(raw, out var artifactId)) return (null, "A selected item is not valid.");

            var visibility = form[$"visibility-{artifactId:D}"].ToString();
            if (visibility is not ("" or "private" or "public"))
                return (null, "The visibility of an item must be private or public.");

            items.Add((artifactId, visibility == "public"));
        }

        return items.Count == 0 ? (null, "Select at least one item to upload.") : (items, null);
    }

    /// <summary>What the form shows again after a refused selection: the ticked items and every visibility choice.</summary>
    private static WorkflowHubFormState ReadFormState(IFormCollection form)
    {
        var checkedItems = form["item"]
            .Select(raw => Guid.TryParse(raw, out var artifactId) ? artifactId : (Guid?)null)
            .OfType<Guid>()
            .ToHashSet();

        const string prefix = "visibility-";
        var publicItems = new Dictionary<Guid, bool>();
        foreach (var key in form.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            if (Guid.TryParse(key[prefix.Length..], out var artifactId))
                publicItems[artifactId] = form[key].ToString() == "public";
        }

        // A second-round form carries the team chosen earlier. It comes back as the hidden field, so a
        // refused selection does not send the user through the team round again.
        int? teamId = int.TryParse(form["team"].ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var team)
                      && team > 0
            ? team
            : null;

        return new WorkflowHubFormState(checkedItems, publicItems, null, teamId);
    }

    /// <summary>
    /// What the form shows again after the callback: the selection from the login cookie, and the teams
    /// to choose from. The chosen team comes back only while no team list is shown, because a listed
    /// team means the earlier choice has to be made again.
    /// </summary>
    private static WorkflowHubFormState FormStateOf(WorkflowHubPendingFlow flow, IReadOnlyList<WorkflowHubTeam>? teams)
        => new(
            flow.Items.Select(item => item.ArtifactId).ToHashSet(),
            flow.Items.ToDictionary(item => item.ArtifactId, item => item.MakePublic),
            teams,
            teams is null ? flow.TeamId : null);

    private static async Task<IResult> RedisplayAsync(
        SubmissionDbContext db,
        Submission submission,
        string statusToken,
        string instanceBase,
        int status,
        string kind,
        string message,
        WorkflowHubFormState formState,
        CancellationToken ct)
    {
        var page = await WorkflowHubPreviewHandlers.RenderViewAsync(
            db, submission, statusToken, instanceBase, WorkflowHubPreviewHandlers.Notice(message, kind), ct, formState);
        return Results.Text(page, "text/html", null, status);
    }

    private static async Task<IFormCollection> ReadForm(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return FormCollection.Empty;
        try
        {
            return await request.ReadFormAsync(ct);
        }
        catch (InvalidDataException)
        {
            return FormCollection.Empty;
        }
    }

    private static IResult InvalidLoginPage()
        => Page(400, "WorkflowHub login no longer valid", $"<p>{Html.Text(InvalidLoginText)}</p>");

    private static string PreviewPath(Guid submissionId, string statusToken)
        => $"/workflowhub/submissions/{submissionId}?t={Uri.EscapeDataString(statusToken)}";

    private static IResult SeeOther(HttpResponse response, string location)
    {
        response.Headers.Location = location;
        return Results.StatusCode(StatusCodes.Status303SeeOther);
    }

    private static bool SameValue(string expected, string? actual)
        => actual is not null
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    private static string WithBackButton(string bodyHtml, string previewPath) => $@"{bodyHtml}
        <div class=""wfh-actions"">
          <a href=""{Html.Text(previewPath)}"" role=""button"" class=""secondary"">Back to the submission</a>
        </div>";

    private static IResult Page(int status, string title, string body, string kind = "error")
        => Results.Text(WorkflowHubResultPages.MessagePage(title, body, kind), "text/html", null, status);
}
