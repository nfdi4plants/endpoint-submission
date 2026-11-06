using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace EndpointSubmission.Tests;

public class WorkflowHubOAuthRouteTests
{
    private const string AccessToken = "wfh-access-token-4711";
    private const string Code = "the-authorization-code";
    private const string RedirectUri = "http://localhost:8081/workflowhub/oauth/callback";
    private const string CookieName = "wfh_oauth";

    /// <summary>
    /// A running app with one WorkflowHub submission holding two ready items, or more with
    /// <c>extraItems</c>. Every response body and redirect target goes into <see cref="Seen"/>, so
    /// each test can prove the access token is in none of them.
    /// </summary>
    private sealed class Site : IDisposable
    {
        private readonly WorkflowHubAppFactory _factory;

        public FakeWorkflowHubGateway Gateway { get; } = new();
        public HttpClient Client { get; }
        public Submission Submission { get; }
        public string StatusToken { get; }
        public PreparedArtifact Align { get; }
        public PreparedArtifact Other { get; }
        public List<PreparedArtifact> Ready { get; }
        public List<string> Seen { get; } = [];

        public Site(
            SubmissionTarget target = SubmissionTarget.Sandbox, bool allowProduction = false, string clientId = "test-client",
            int extraItems = 0)
        {
            _factory = new WorkflowHubAppFactory(Gateway, allowProduction, clientId);
            Client = NewClient();

            var minted = new StatusTokenService().Mint();
            StatusToken = minted.Token;
            Submission = new Submission
            {
                ProjectName = "group/my-arc",
                DataHubHost = "git.example.org",
                Endpoint = "workflowhub",
                Target = target,
                StatusTokenHash = minted.Hash,
                Status = SubmissionStatus.PreviewReady
            };
            Align = Artifact("workflows/align", [1, 2, 3]);
            Other = Artifact("workflows/other", [4, 5]);
            Ready = [Align, Other, .. Enumerable.Range(1, extraItems).Select(i => Artifact($"workflows/extra-{i}", [(byte)i]))];

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SubmissionDbContext>();
            db.Submissions.Add(Submission);
            db.PreparedArtifacts.AddRange(Ready);
            db.SaveChanges();
        }

        private PreparedArtifact Artifact(string identifier, byte[] content) => new()
        {
            SubmissionId = Submission.Id,
            ItemKind = "workflow",
            ItemIdentifier = identifier,
            Status = PreparedArtifactStatus.Ready,
            Content = content,
            SizeBytes = content.Length,
            ContentSha256 = "sha-" + identifier
        };

        /// <summary>A client with a cookie jar of its own, like one more browser.</summary>
        public HttpClient NewClient(bool handleCookies = true)
            => _factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                HandleCookies = handleCookies
            });

        public string PreviewPath => $"/workflowhub/submissions/{Submission.Id}?t={StatusToken}";

        public string BackLink(string cssClass = "secondary")
            => $"""<a href="{PreviewPath}" role="button" class="{cssClass}">Back to the submission</a>""";

        public List<KeyValuePair<string, string>> Form(
            (PreparedArtifact Artifact, string Visibility)[] items, string? team = null, string? t = null)
        {
            var fields = new List<KeyValuePair<string, string>> { new("t", t ?? StatusToken) };
            foreach (var (artifact, visibility) in items)
            {
                fields.Add(new("item", artifact.Id.ToString()));
                fields.Add(new($"visibility-{artifact.Id}", visibility));
            }

            if (team is not null) fields.Add(new("team", team));
            return fields;
        }

        public Task<(HttpResponseMessage Response, string Body)> Authorize(
            List<KeyValuePair<string, string>> fields, HttpClient? client = null)
            => Send(() => (client ?? Client).PostAsync(
                $"/workflowhub/submissions/{Submission.Id}/authorize", new FormUrlEncodedContent(fields)));

        public Task<(HttpResponseMessage Response, string Body)> Callback(string query, HttpClient? client = null)
            => Send(() => (client ?? Client).GetAsync("/workflowhub/oauth/callback?" + query));

        public Task<(HttpResponseMessage Response, string Body)> Get(string path, HttpClient? client = null)
            => Send(() => (client ?? Client).GetAsync(path));

        /// <summary>Posts the form, expects the redirect to WorkflowHub and returns what the login cookie holds.</summary>
        public async Task<WorkflowHubPendingFlow> StartLogin(
            (PreparedArtifact Artifact, string Visibility)[] items, string? team = null, HttpClient? client = null)
        {
            var (response, _) = await Authorize(Form(items, team), client);
            Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
            return Unprotect(CookieValue(response));
        }

        /// <summary>The callback query WorkflowHub sends back after the user confirmed.</summary>
        public static string Confirmed(WorkflowHubPendingFlow flow) => $"code={Code}&state={flow.State}";

        public static string CookieValue(HttpResponseMessage response)
        {
            var header = Assert.Single(
                response.Headers.GetValues("Set-Cookie"), h => h.StartsWith(CookieName + "=", StringComparison.Ordinal));
            return Uri.UnescapeDataString(header.Split(';')[0][(CookieName.Length + 1)..]);
        }

        public WorkflowHubPendingFlow Unprotect(string cookieValue)
        {
            var protector = _factory.Services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector(WorkflowHubFlowCookie.Purpose);
            return JsonSerializer.Deserialize<WorkflowHubPendingFlow>(protector.Unprotect(cookieValue))!;
        }

        private async Task<(HttpResponseMessage, string)> Send(Func<Task<HttpResponseMessage>> call)
        {
            var response = await call();
            var body = await response.Content.ReadAsStringAsync();
            // Concurrent requests finish on different threads.
            lock (Seen)
            {
                Seen.Add(body);
                if (response.Headers.Location is { } location) Seen.Add(location.ToString());
            }

            return (response, body);
        }

        public SubmissionDbContext NewContext()
            => _factory.Services.CreateScope().ServiceProvider.GetRequiredService<SubmissionDbContext>();

        public List<string> Logs()
        {
            lock (_factory.Logs) return [.. _factory.Logs];
        }

        /// <summary>The token reached the fake WorkflowHub only. It is in no page, header, log line or stored string.</summary>
        public async Task AssertTokenNowhere()
        {
            Assert.All(Seen, text => Assert.DoesNotContain(AccessToken, text));
            Assert.All(Logs(), line => Assert.DoesNotContain(AccessToken, line));
            await StoredStrings.AssertNoneContain(NewContext(), AccessToken);
        }

        public void Dispose()
        {
            Client.Dispose();
            _factory.Dispose();
        }
    }

    private static (PreparedArtifact, string)[] Pick(PreparedArtifact artifact, string visibility) => [(artifact, visibility)];

    private static string Base64Url(byte[] bytes) => WebEncoders.Base64UrlEncode(bytes);

    private static bool Deletes(HttpResponseMessage response)
        => response.Headers.GetValues("Set-Cookie").Any(h =>
            h.StartsWith(CookieName + "=;", StringComparison.Ordinal)
            && h.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase)
            && h.Contains("path=/workflowhub", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Authorize_with_a_wrong_status_token_gets_a_404_and_no_cookie()
    {
        using var site = new Site();

        var (response, _) = await site.Authorize(site.Form(Pick(site.Align, "private"), t: "wrong"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(site.Gateway.Exchanges);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task The_production_target_is_refused_while_the_switch_is_off()
    {
        using var site = new Site(SubmissionTarget.Production, allowProduction: false);

        var (response, html) = await site.Authorize(site.Form(Pick(site.Align, "private")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Uploading to the production WorkflowHub is not enabled on this deployment.", html);
        Assert.Contains(@"<article class=""wfh-message wfh-message-warning"">", html);
        Assert.Contains("<h1>Production upload is off</h1>", html);
        Assert.Contains(site.BackLink(), html);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_target_without_a_client_id_answers_503_with_a_link_back()
    {
        using var site = new Site(clientId: "");

        var (response, html) = await site.Authorize(site.Form(Pick(site.Align, "private")));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("WorkflowHub login is not configured on this deployment.", html);
        Assert.Contains(site.BackLink(), html);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_selection_without_items_shows_the_form_again_with_a_notice()
    {
        using var site = new Site();

        var (response, html) = await site.Authorize(site.Form([]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Select at least one item to upload.", html);
        Assert.Contains(@"<div class=""wfh-notice wfh-notice-error"" role=""alert"">", html);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_refused_second_round_form_keeps_the_chosen_team()
    {
        using var site = new Site();

        var (response, html) = await site.Authorize(site.Form([], team: "7"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Select at least one item to upload.", html);
        Assert.Contains("""<input type="hidden" name="team" value="7">""", html);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task More_than_25_items_are_refused_with_the_ticks_kept_and_no_login_starts()
    {
        using var site = new Site(extraItems: 24);
        var fields = site.Form([.. site.Ready.Select(artifact => (artifact, "private"))]);

        var (response, html) = await site.Authorize(fields);

        Assert.Equal(26, site.Ready.Count);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("One upload takes at most 25 items. Select fewer and upload the rest afterwards.", html);
        Assert.Contains(@"<div class=""wfh-notice wfh-notice-error"" role=""alert"">", html);
        Assert.All(site.Ready, artifact => Assert.Contains($"""name="item" value="{artifact.Id}" checked""", html));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Null(response.Headers.Location);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task The_login_cookie_for_25_items_stays_below_the_size_a_browser_keeps()
    {
        using var site = new Site(extraItems: 23);

        var (response, _) = await site.Authorize(site.Form([.. site.Ready.Select(artifact => (artifact, "public"))]));

        Assert.Equal(25, site.Ready.Count);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.True(cookie.Length < 4000, $"The Set-Cookie header has {cookie.Length} characters.");
        Assert.Equal(25, site.Unprotect(Site.CookieValue(response)).Items.Count);
    }

    [Fact]
    public async Task An_item_of_another_submission_is_refused_and_the_ticks_and_visibility_stay()
    {
        using var site = new Site();
        var fields = site.Form([(site.Align, "public")]);
        fields.Add(new("item", Guid.NewGuid().ToString()));

        var (response, html) = await site.Authorize(fields);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("A selected item does not belong to this submission.", html);
        Assert.Contains($"""name="item" value="{site.Align.Id}" checked""", html);
        Assert.DoesNotContain($"""name="item" value="{site.Other.Id}" checked""", html);
        Assert.Contains("""<option value="public" selected>Public</option>""", html);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_team_value_that_is_not_a_positive_number_is_refused()
    {
        using var site = new Site();

        foreach (var team in new[] { "abc", "0", "-5", "" })
        {
            var (response, html) = await site.Authorize(site.Form(Pick(site.Align, "private"), team: team));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("The chosen WorkflowHub team is not valid.", html);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }

        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task Authorize_sends_the_user_to_WorkflowHub_with_a_PKCE_challenge_and_stores_the_flow_in_a_cookie()
    {
        using var site = new Site();

        var (response, _) = await site.Authorize(site.Form([(site.Align, "public"), (site.Other, "private")]));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var location = response.Headers.Location!;
        Assert.Equal("https://dev.workflowhub.eu/oauth/authorize", location.GetLeftPart(UriPartial.Path));

        var query = HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("test-client", query["client_id"]);
        Assert.Equal(RedirectUri, query["redirect_uri"]);
        Assert.Equal("read write", query["scope"]);
        Assert.Equal("S256", query["code_challenge_method"]);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith(CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/workflowhub", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=600", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure;", cookie + ";", StringComparison.OrdinalIgnoreCase);

        var flow = site.Unprotect(Site.CookieValue(response));
        Assert.Equal(site.Submission.Id, flow.SubmissionId);
        Assert.Equal(site.StatusToken, flow.StatusToken);
        Assert.Equal(
            new[] { (site.Align.Id, true), (site.Other.Id, false) },
            flow.Items.Select(item => (item.ArtifactId, item.MakePublic)).ToArray());
        Assert.Null(flow.TeamId);
        Assert.Equal(flow.State, query["state"]);
        Assert.Equal(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(flow.CodeVerifier))), query["code_challenge"]);
        Assert.True(flow.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(9));
        Assert.DoesNotContain(flow.CodeVerifier, Site.CookieValue(response));
        Assert.Empty(site.Gateway.Exchanges);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_callback_without_a_readable_cookie_is_refused()
    {
        using var site = new Site();
        using var withoutCookies = site.NewClient(handleCookies: false);
        using var damaged = new HttpRequestMessage(HttpMethod.Get, "/workflowhub/oauth/callback?code=c&state=s");
        damaged.Headers.Add("Cookie", CookieName + "=not-a-protected-value");

        var (missing, html) = await site.Callback("code=c&state=s");
        var damagedResponse = await withoutCookies.SendAsync(damaged);

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, damagedResponse.StatusCode);
        Assert.Contains("This WorkflowHub login is no longer valid. Open your submission from the badge link and start again.", html);
        Assert.DoesNotContain("Back to the submission", html);
        Assert.True(Deletes(missing));
        Assert.True(Deletes(damagedResponse));
        Assert.Empty(site.Gateway.Exchanges);
        Assert.Empty(site.Gateway.Revokes);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_callback_with_another_state_is_refused_and_leaves_the_login_in_progress()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (mismatch, html) = await site.Callback($"code={Code}&state=another-state");

        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Contains("This WorkflowHub login is no longer valid.", html);
        // A forged or stale callback must not delete the cookie of the login in progress.
        Assert.False(mismatch.Headers.Contains("Set-Cookie"));
        Assert.Empty(site.Gateway.Exchanges);
        Assert.Empty(site.Gateway.TeamLists);
        Assert.Empty(site.Gateway.Uploads);

        var (retry, _) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.SeeOther, retry.StatusCode);
        Assert.True(Deletes(retry));
        Assert.Single(site.Gateway.Exchanges);
        Assert.Single(site.Gateway.Uploads);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_callback_with_neither_code_nor_error_is_refused_and_leaves_the_cookie()
    {
        using var site = new Site();
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, html) = await site.Callback($"state={flow.State}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("This WorkflowHub login is no longer valid.", html);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Empty(site.Gateway.Exchanges);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_login_the_user_did_not_confirm_uploads_nothing_and_restores_the_form()
    {
        using var site = new Site();
        var flow = await site.StartLogin(Pick(site.Align, "public"));

        var (response, html) = await site.Callback($"error=access_denied&state={flow.State}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("You did not confirm the upload on WorkflowHub. Nothing was uploaded.", html);
        Assert.Contains(@"<div class=""wfh-notice wfh-notice-warning"" role=""alert"">", html);
        Assert.Contains($"""name="item" value="{site.Align.Id}" checked""", html);
        Assert.DoesNotContain($"""name="item" value="{site.Other.Id}" checked""", html);
        Assert.Contains("""<option value="public" selected>Public</option>""", html);
        Assert.True(Deletes(response));
        Assert.Empty(site.Gateway.Exchanges);
        Assert.Empty(site.Gateway.Revokes);
        Assert.Empty(site.Gateway.Uploads);
        Assert.Empty(site.NewContext().Accessions);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_declined_second_round_keeps_the_chosen_team_in_a_hidden_field()
    {
        using var site = new Site();
        var flow = await site.StartLogin(Pick(site.Align, "private"), team: "7");
        Assert.Equal(7, flow.TeamId);

        var (response, html) = await site.Callback($"error=access_denied&state={flow.State}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("""<input type="hidden" name="team" value="7">""", html);
        Assert.DoesNotContain("""type="radio" """, html);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task Another_error_from_WorkflowHub_shows_a_refusal_and_logs_the_code()
    {
        using var site = new Site();
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, html) = await site.Callback($"error=invalid_scope&state={flow.State}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WorkflowHub refused the login. Nothing was uploaded.", html);
        Assert.Contains(@"<div class=""wfh-notice wfh-notice-error"" role=""alert"">", html);
        Assert.DoesNotContain("You did not confirm the upload", html);
        Assert.True(Deletes(response));
        Assert.Contains(site.Logs(), line => line.Contains("refused the login") && line.Contains("invalid_scope"));
        Assert.Empty(site.Gateway.Exchanges);
        Assert.Empty(site.Gateway.Uploads);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task An_unknown_error_value_is_logged_as_other_and_never_shown()
    {
        const string markup = "<script>alert(1)</script>";
        using var site = new Site();
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, html) = await site.Callback($"error={Uri.EscapeDataString(markup)}&state={flow.State}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WorkflowHub refused the login. Nothing was uploaded.", html);
        Assert.DoesNotContain("alert(1)", html);
        Assert.Contains(site.Logs(), line => line.Contains("refused the login") && line.Contains("with error other"));
        Assert.All(site.Logs(), line => Assert.DoesNotContain("alert(1)", line));
        Assert.Empty(site.Gateway.Exchanges);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task One_team_uploads_with_the_chosen_visibility_revokes_the_token_and_redirects_to_the_preview()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        var flow = await site.StartLogin([(site.Align, "public"), (site.Other, "private")]);

        var (response, _) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(
            $"http://localhost:8081/workflowhub/submissions/{site.Submission.Id}?t={site.StatusToken}&uploaded=2&failed=0&unknown=0&partial=0&skipped=0",
            response.Headers.Location!.ToString());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(Deletes(response));

        Assert.Equal(
            ("https://dev.workflowhub.eu", "test-client", Code, RedirectUri, flow.CodeVerifier),
            Assert.Single(site.Gateway.Exchanges));
        Assert.Equal(("https://dev.workflowhub.eu", AccessToken), Assert.Single(site.Gateway.TeamLists));
        Assert.Collection(site.Gateway.Uploads,
            u =>
            {
                Assert.Equal(("https://dev.workflowhub.eu", AccessToken, 5, true), (u.BaseUrl, u.Token, u.TeamId, u.MakePublic));
                Assert.Equal(site.Align.Content, u.Crate);
            },
            u =>
            {
                Assert.Equal((AccessToken, 5, false), (u.Token, u.TeamId, u.MakePublic));
                Assert.Equal(site.Other.Content, u.Crate);
            });
        Assert.Equal(
            ("https://dev.workflowhub.eu", "test-client", AccessToken), Assert.Single(site.Gateway.Revokes));
        Assert.Equal(
            new[] { "workflow/workflows/align", "workflow/workflows/other" },
            site.NewContext().Accessions.Select(a => a.Alias).OrderBy(a => a).ToArray());

        // The browser follows the redirect to the preview, which turns the counts into a banner.
        var (preview, html) = await site.Get(response.Headers.Location!.PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Contains("<p>2 items uploaded to WorkflowHub.</p>", html);
        Assert.DoesNotContain("<form", html);

        // The code and the verifier reach no page, header or log line. The state goes to WorkflowHub
        // in the authorize redirect and to no log line.
        Assert.All(site.Seen.Concat(site.Logs()), text =>
        {
            Assert.DoesNotContain(Code, text);
            Assert.DoesNotContain(flow.CodeVerifier, text);
        });
        Assert.All(site.Logs(), line => Assert.DoesNotContain(flow.State, line));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task An_account_without_a_team_gets_the_no_team_page_and_the_token_is_revoked()
    {
        using var site = new Site();
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, html) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("requires membership in a WorkflowHub team", html);
        Assert.Contains("<h1>Nothing was uploaded</h1>", html);
        Assert.Contains(
            """<a href="https://dev.workflowhub.eu/projects" target="_blank" rel="noopener noreferrer" role="button">WorkflowHub teams</a>""",
            html);
        Assert.Contains(site.BackLink("outline"), html);
        Assert.Empty(site.Gateway.Uploads);
        Assert.Single(site.Gateway.Revokes);
        Assert.Equal("upload.no_team", Assert.Single(site.NewContext().SubmissionEvents).Step);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task Several_teams_show_the_picker_and_a_second_login_uploads_to_the_chosen_team()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team <b>Five</b>"), new WorkflowHubTeam(9, "Team Nine")];
        var first = await site.StartLogin(Pick(site.Align, "public"));

        var (picker, html) = await site.Callback(Site.Confirmed(first));

        Assert.Equal(HttpStatusCode.OK, picker.StatusCode);
        Assert.Contains(
            "You are a member of several WorkflowHub teams. Choose the team to upload to, then continue. WorkflowHub asks you to confirm once more.",
            html);
        Assert.Contains(@"<div class=""wfh-notice wfh-notice-info"" role=""alert"">", html);
        Assert.Contains("<legend>WorkflowHub team</legend>", html);
        Assert.Contains("Team &lt;b&gt;Five&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>", html);
        Assert.Contains("""<input type="radio" name="team" value="9" required>""", html);
        Assert.Contains($"""name="item" value="{site.Align.Id}" checked""", html);
        Assert.Contains("""<option value="public" selected>Public</option>""", html);
        Assert.Contains($"/workflowhub/submissions/{site.Submission.Id}/authorize", html);
        Assert.Empty(site.Gateway.Uploads);
        Assert.Single(site.Gateway.Revokes);

        var second = await site.StartLogin(Pick(site.Align, "public"), team: "9");
        Assert.Equal(9, second.TeamId);
        var (done, _) = await site.Callback(Site.Confirmed(second));

        Assert.Equal(HttpStatusCode.SeeOther, done.StatusCode);
        var upload = Assert.Single(site.Gateway.Uploads);
        Assert.Equal((AccessToken, 9, true), (upload.Token, upload.TeamId, upload.MakePublic));
        Assert.Equal(2, site.Gateway.Revokes.Count);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_chosen_team_that_is_not_one_of_the_users_teams_is_refused()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five"), new WorkflowHubTeam(9, "Team Nine")];
        var flow = await site.StartLogin(Pick(site.Align, "private"), team: "77");

        var (response, html) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("You are not a member of the chosen WorkflowHub team. Choose another one.", html);
        Assert.Contains("""<input type="radio" name="team" value="5" required>""", html);
        Assert.Empty(site.Gateway.Uploads);
        Assert.Single(site.Gateway.Revokes);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_refused_team_with_one_team_left_does_not_keep_the_old_choice()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        var flow = await site.StartLogin(Pick(site.Align, "private"), team: "77");

        var (response, html) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("You are not a member of the chosen WorkflowHub team. Choose another one.", html);
        Assert.DoesNotContain("""name="team" """, html);
        Assert.DoesNotContain("""name="team">""", html);
        Assert.Empty(site.Gateway.Uploads);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_failed_code_exchange_answers_502_and_uploads_nothing()
    {
        using var site = new Site();
        site.Gateway.ExchangeFails = new WorkflowHubGatewayException(
            WorkflowHubFailureKind.Rejected, 400, $"invalid_grant: {Code}", null);
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, html) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("<h1>WorkflowHub login failed</h1>", html);
        Assert.Contains(site.BackLink(), html);
        Assert.Empty(site.Gateway.TeamLists);
        Assert.Empty(site.Gateway.Uploads);
        Assert.Empty(site.Gateway.Revokes);
        Assert.Contains(site.Logs(), line =>
            line.Contains("code exchange failed") && line.Contains("Rejected") && line.Contains("status 400"));
        Assert.All(site.Logs().Concat(site.Seen), text => Assert.DoesNotContain(Code, text));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_token_that_WorkflowHub_does_not_accept_shows_the_form_again_and_revokes_it()
    {
        using var site = new Site();
        site.Gateway.ListFails = new WorkflowHubGatewayException(WorkflowHubFailureKind.Rejected, 404, "No user logged in", null);
        var flow = await site.StartLogin(Pick(site.Align, "public"));

        var (response, html) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("WorkflowHub did not accept the login. Start the upload again.", html);
        Assert.Contains($"""name="item" value="{site.Align.Id}" checked""", html);
        Assert.Empty(site.Gateway.Uploads);
        Assert.Single(site.Gateway.Revokes);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task An_unreachable_WorkflowHub_gets_a_502_page_with_a_link_back_and_the_token_is_revoked()
    {
        using var site = new Site();
        site.Gateway.ListFails = new WorkflowHubGatewayException(WorkflowHubFailureKind.Transport, null, "no route", null);
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, html) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("WorkflowHub could not be reached or answered with an error. Nothing was uploaded.", html);
        Assert.Contains(site.BackLink(), html);
        Assert.Empty(site.Gateway.Uploads);
        Assert.Single(site.Gateway.Revokes);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task An_upload_without_a_usable_answer_counts_as_unknown_and_the_token_is_still_revoked()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        site.Gateway.Respond = (n, makePublic) => n == 0
            ? throw new WorkflowHubGatewayException(
                WorkflowHubFailureKind.OutcomeUnknown, null, "no answer", null, rawBody: $"half an answer with {AccessToken}")
            : new WorkflowHubUpload(
                41, "https://dev.workflowhub.eu/workflows/41?version=1", 1, makePublic, "{}", "{}", "{}");
        var flow = await site.StartLogin([(site.Align, "private"), (site.Other, "private")]);

        var (response, _) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.EndsWith("&uploaded=1&failed=0&unknown=1&partial=0&skipped=0", response.Headers.Location!.ToString());
        Assert.Single(site.Gateway.Revokes);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task An_exception_that_leaves_the_handler_still_revokes_the_token()
    {
        using var site = new Site();
        site.Gateway.ListFails = new InvalidOperationException($"boom");
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, _) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(("https://dev.workflowhub.eu", "test-client", AccessToken), Assert.Single(site.Gateway.Revokes));
        Assert.Empty(site.Gateway.Uploads);
        Assert.All(site.Seen, text => Assert.DoesNotContain(AccessToken, text));
    }

    [Fact]
    public async Task A_failing_revoke_does_not_change_the_answer_and_only_the_exception_type_is_logged()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        site.Gateway.RevokeFails = new InvalidOperationException($"revoke failed for {AccessToken}");
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, _) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Single(site.Gateway.Revokes);
        Assert.Contains(site.Logs(), line => line.Contains("Revoking") && line.Contains("InvalidOperationException"));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_revoke_that_WorkflowHub_rejects_logs_the_status_and_nothing_else_from_the_failure()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        site.Gateway.RevokeFails = new WorkflowHubGatewayException(
            WorkflowHubFailureKind.Rejected, 503, $"revoke detail for {AccessToken}", null);
        var flow = await site.StartLogin(Pick(site.Align, "private"));

        var (response, _) = await site.Callback(Site.Confirmed(flow));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Contains(site.Logs(), line =>
            line.Contains("Revoking") && line.Contains("WorkflowHubGatewayException") && line.Contains("status 503"));
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task A_token_that_arrives_after_the_request_was_cancelled_is_still_revoked_once()
    {
        using var site = new Site();
        var flow = await site.StartLogin(Pick(site.Align, "private"));
        using var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Like the real gateway, the fake gives up when the token it received is cancelled.
        site.Gateway.BeforeExchange = async ct =>
        {
            entered.Release();
            await release.Task.WaitAsync(ct);
        };
        using var closedTab = new CancellationTokenSource();

        var callback = site.Client.GetAsync("/workflowhub/oauth/callback?" + Site.Confirmed(flow), closedTab.Token);
        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)), "The exchange was not reached.");
        await closedTab.CancelAsync();
        release.SetResult();
        try
        {
            (await callback).Dispose();
        }
        catch (OperationCanceledException)
        {
            // The client gave up on the response. The handler goes on without it.
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (site.Gateway.Revokes.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        // A second revoke would show up within this delay.
        await Task.Delay(200);

        Assert.Equal(("https://dev.workflowhub.eu", "test-client", AccessToken), Assert.Single(site.Gateway.Revokes));
        Assert.Empty(site.Gateway.Uploads);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task Two_concurrent_callbacks_of_one_submission_send_the_crate_to_WorkflowHub_once()
    {
        using var site = new Site();
        site.Gateway.Teams = [new WorkflowHubTeam(5, "Team Five")];
        using var otherBrowser = site.NewClient();
        var first = await site.StartLogin(Pick(site.Align, "private"));
        var second = await site.StartLogin(Pick(site.Align, "private"), client: otherBrowser);

        using var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Gateway.BeforeUpload = async () =>
        {
            entered.Release();
            await release.Task;
        };

        var firstCallback = site.Callback(Site.Confirmed(first));
        var secondCallback = site.Callback(Site.Confirmed(second), otherBrowser);

        Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(10)), "No upload reached the gateway.");
        // The first upload is held open inside the lock. This delay lets the second callback pass
        // its checks and queue behind it.
        await Task.Delay(300);
        release.SetResult();
        var results = await Task.WhenAll(firstCallback, secondCallback).WaitAsync(TimeSpan.FromSeconds(10));

        var refused = Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.BadRequest);
        Assert.Single(results, r => r.Response.StatusCode == HttpStatusCode.SeeOther);
        Assert.Contains("was uploaded already", refused.Body);
        Assert.Single(site.Gateway.Uploads);
        Assert.Single(site.NewContext().Accessions.Where(a => a.SubmissionId == site.Submission.Id));
        Assert.Equal(2, site.Gateway.Revokes.Count);
        await site.AssertTokenNowhere();
    }

    [Fact]
    public async Task The_preview_form_posts_to_authorize_asks_for_no_token_and_is_not_cached()
    {
        using var site = new Site();

        var (response, html) = await site.Get(site.PreviewPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains($"""action="/workflowhub/submissions/{site.Submission.Id}/authorize" """, html);
        Assert.Contains("""<button type="submit" data-wfh-submit>Continue to WorkflowHub</button>""", html);
        Assert.Contains("""role="status" aria-live="polite" data-wfh-status""", html);
        Assert.DoesNotContain("""type="password" """, html);
        await site.AssertTokenNowhere();
    }
}
