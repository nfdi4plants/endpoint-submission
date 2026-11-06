using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

// Test instance that stands in for the DataHUB validation badge. It shares no code with the
// endpoint submission service and sends the same hook request AaaS would send.

const string BackLink = """<p><a href="/">Back to the mock badge</a></p>""";

var config = MockConfig.FromEnvironment();

// Every hook request carries the DataHUB token, so it must not travel over plain http to a
// remote host. The message names the variable and never prints its value.
if (config.BaseUrl is not null
    && Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri)
    && !MockConfig.IsAllowedHookBase(baseUri))
{
    Console.Error.WriteLine(
        "MOCK_HOOK_BASE_URL must use https. Plain http is accepted only for localhost, 127.0.0.1 " +
        "or a host name without a dot, such as a compose service name. The mock does not start.");
    Environment.Exit(1);
}

var builder = WebApplication.CreateBuilder(args);

// The hook call is slow because the service checks the ARC out and packs crates. This client
// has no logging handler, so no request detail reaches the log.
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

var app = builder.Build();

if (!config.IsUsable)
{
    // Variable names only, never values.
    app.Logger.LogWarning("Configuration incomplete. Missing: {Missing}. Invalid: {Invalid}.",
        string.Join(", ", config.Missing), string.Join(", ", config.Invalid));
}

app.MapGet("/", () => Html(IndexPage(config)));

app.MapPost("/click/workflowhub", async (HttpContext context, HttpClient client, CancellationToken ct) =>
{
    // A page on another site could post a form here and spend the configured token.
    if (!IsSameOrigin(context.Request))
    {
        return Html(Page("Request refused", $"<h1>Request refused</h1><p>The request did not come from this page.</p>{BackLink}"), 403);
    }

    if (!config.IsUsable)
    {
        return Html(Page("Configuration problem", $"<h1>Configuration problem</h1>{ProblemsHtml(config)}{BackLink}"), 400);
    }

    var body = JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        ["project_id"] = config.ProjectId,
        ["project_name"] = config.ProjectName,
        ["gitlab_token"] = config.Pat,
        ["ro_crate_url"] = config.RoCrateUrl,
        ["package_payload"] = new Dictionary<string, object?>
        {
            ["contract_version"] = 1,
            ["target"] = config.Target
        }
    });

    using var request = new HttpRequestMessage(
        HttpMethod.Post, $"{config.BaseUrl!.TrimEnd('/')}/api/v1/hooks/workflowhub")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
    if (config.HookSecret is not null)
    {
        request.Headers.Add("X-Hook-Secret", config.HookSecret);
    }

    try
    {
        using var response = await client.SendAsync(request, ct);
        var status = (int)response.StatusCode;
        var text = await response.Content.ReadAsStringAsync(ct);
        app.Logger.LogInformation("Hook answered with status {Status}.", status);

        if (status == 200 && TryReadFollowMe(text, out var followMe))
        {
            return new SeeOther(followMe);
        }

        var message = status == 200
            ? "The service answered 200 without a usable follow_me URL."
            : ReadErrorText(text);
        return Html(FailurePage($"Status code: {status}", message), 502);
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning("Hook request ended without a response.");
        return Html(FailurePage("The request to the service did not complete.", $"Exception type: {ex.GetType().Name}"), 502);
    }
});

app.Run();

// ---- helpers ----

static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

static IResult Html(string html, int status = 200) =>
    Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, status);

// The mock listens on loopback, so a request under any other host name is a DNS rebinding attempt.
// Browsers send Sec-Fetch-Site and Origin on a form post. curl sends neither, so it passes.
static bool IsSameOrigin(HttpRequest request)
{
    var host = request.Host.Host;
    if (!string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
    {
        return false;
    }

    if (request.Headers.TryGetValue("Sec-Fetch-Site", out var site)
        && !string.Equals(site.ToString(), "same-origin", StringComparison.Ordinal))
    {
        return false;
    }

    if (request.Headers.TryGetValue("Origin", out var origin))
    {
        return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var originUri)
            && string.Equals(originUri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    return true;
}

string Redact(string text) =>
    string.IsNullOrEmpty(config.Pat) ? text : text.Replace(config.Pat, "[REDACTED]", StringComparison.Ordinal);

string FailurePage(string headline, string detail) =>
    Page("Hook request failed", $"""
        <h1>Hook request failed</h1>
        <p>{E(Redact(headline))}</p>
        <p>{E(Redact(detail))}</p>
        {BackLink}
        """);

static bool TryReadFollowMe(string json, out string followMe)
{
    followMe = string.Empty;
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("follow_me", out var value)
            && value.ValueKind == JsonValueKind.String
            && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            followMe = uri.AbsoluteUri;
            return true;
        }
    }
    catch (JsonException)
    {
    }
    return false;
}

static string ReadErrorText(string json)
{
    try
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            // 400, 401 and 502 carry "error". The 503 problem JSON carries "detail".
            foreach (var name in new[] { "error", "detail" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString()!;
                }
            }
        }
    }
    catch (JsonException)
    {
    }
    return "The response carried no error text.";
}

static string ProblemsHtml(MockConfig c)
{
    var sb = new StringBuilder("<p>Configuration problems (variable names only):</p><ul>");
    foreach (var name in c.Missing) sb.Append($"<li>{E(name)} is missing</li>");
    foreach (var name in c.Invalid) sb.Append($"<li>{E(name)} is invalid</li>");
    return sb.Append("</ul>").ToString();
}

static string IndexPage(MockConfig c)
{
    var project = c.ProjectName is not null && c.ProjectIdValid ? $"{E(c.ProjectName)} (id {c.ProjectId})" : "not configured";
    var host = c.DataHubHost is null ? "not configured" : E(c.DataHubHost);
    var target = c.TargetValid ? E(c.Target) : "invalid";
    var disabled = c.IsUsable ? string.Empty : " disabled";
    var problems = c.IsUsable ? string.Empty : ProblemsHtml(c);

    return Page("Mock badge", $$"""
        <h1>Mock validation badge</h1>
        <dl>
          <dt>Project</dt><dd>{{project}}</dd>
          <dt>DataHUB host</dt><dd>{{host}}</dd>
          <dt>Target</dt><dd>{{target}}</dd>
          <dt>Token</dt><dd>token configured: {{(string.IsNullOrEmpty(c.Pat) ? "no" : "yes")}}</dd>
        </dl>
        {{problems}}
        <form method="post" action="/click/workflowhub">
          <button type="submit"{{disabled}}>Submit to WorkflowHub (sandbox)</button>
        </form>
        <p>The service checks the ARC out and packs crates before it answers, so a click can take several minutes.</p>
        """);
}

static string Page(string title, string body) => $$"""
    <!doctype html>
    <html lang="en">
    <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>{{E(title)}}</title>
    <style>
    body { font-family: sans-serif; margin: 0; padding: 0 16px 32px; max-width: 48rem; }
    .banner { background: #ffe08a; color: #3d2f00; border: 2px solid #8a6d00; padding: 12px 16px; margin: 16px 0; font-weight: bold; }
    dt { font-weight: bold; margin-top: 8px; }
    dd { margin-left: 0; }
    button { font-size: 1rem; padding: 8px 16px; }
    </style>
    </head>
    <body>
    <div class="banner">Test instance: this page stands in for the DataHUB validation badge. It is not part of the endpoint submission service.</div>
    {{body}}
    </body>
    </html>
    """;

sealed class SeeOther(string location) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status303SeeOther;
        httpContext.Response.Headers.Location = location;
        return Task.CompletedTask;
    }
}

sealed class MockConfig
{
    public string? BaseUrl { get; private set; }
    public int ProjectId { get; private set; }
    public bool ProjectIdValid { get; private set; }
    public string? ProjectName { get; private set; }
    public string? RoCrateUrl { get; private set; }
    public string? DataHubHost { get; private set; }
    public string? Pat { get; private set; }
    public string Target { get; private set; } = "sandbox";
    public bool TargetValid { get; private set; } = true;
    public string? HookSecret { get; private set; }

    public List<string> Missing { get; } = new();
    public List<string> Invalid { get; } = new();

    public bool IsUsable => Missing.Count == 0 && Invalid.Count == 0;

    public static MockConfig FromEnvironment()
    {
        var c = new MockConfig();

        c.BaseUrl = c.Required("MOCK_HOOK_BASE_URL");
        if (c.BaseUrl is not null && !IsHttpUrl(c.BaseUrl, out _)) c.Invalid.Add("MOCK_HOOK_BASE_URL");

        var rawId = c.Required("MOCK_PROJECT_ID");
        if (rawId is not null)
        {
            if (int.TryParse(rawId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                c.ProjectId = id;
                c.ProjectIdValid = true;
            }
            else
            {
                c.Invalid.Add("MOCK_PROJECT_ID");
            }
        }

        c.ProjectName = c.Required("MOCK_PROJECT_NAME");

        c.RoCrateUrl = c.Required("MOCK_RO_CRATE_URL");
        if (c.RoCrateUrl is not null)
        {
            if (IsHttpUrl(c.RoCrateUrl, out var crateUri)) c.DataHubHost = crateUri!.Host;
            else c.Invalid.Add("MOCK_RO_CRATE_URL");
        }

        c.Pat = c.Required("MOCK_DATAHUB_PAT");

        // This instance never targets production, so anything but sandbox is a configuration error.
        var target = Read("MOCK_TARGET");
        if (target is not null && target != "sandbox")
        {
            c.TargetValid = false;
            c.Invalid.Add("MOCK_TARGET");
        }

        c.HookSecret = Read("MOCK_HOOK_SECRET");
        return c;
    }

    private string? Required(string name)
    {
        var value = Read(name);
        if (value is null) Missing.Add(name);
        return value;
    }

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static bool IsAllowedHookBase(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp
            && (uri.Host is "localhost" or "127.0.0.1" || !uri.Host.Contains('.')));

    private static bool IsHttpUrl(string value, out Uri? uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
