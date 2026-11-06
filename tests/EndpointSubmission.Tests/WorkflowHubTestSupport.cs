using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EndpointSubmission.Tests;

/// <summary>
/// Answers WorkflowHub calls from a script and records what it was asked, so tests can assert
/// the access token and the crates reached WorkflowHub's side and nowhere else.
/// </summary>
internal sealed class FakeWorkflowHubGateway : IWorkflowHubGateway
{
    /// <summary>The access token <see cref="ExchangeCodeAsync"/> hands out.</summary>
    public string AccessToken { get; set; } = "wfh-access-token-4711";

    /// <summary>Thrown by <see cref="ExchangeCodeAsync"/> when set.</summary>
    public Exception? ExchangeFails { get; set; }

    /// <summary>Thrown by <see cref="RevokeAsync"/> after it records the call, when set.</summary>
    public Exception? RevokeFails { get; set; }

    public IReadOnlyList<WorkflowHubTeam> Teams { get; set; } = [];

    /// <summary>Thrown by <see cref="ListTeamsAsync"/> when set.</summary>
    public Exception? ListFails { get; set; }

    /// <summary>Called with the zero-based call number and the requested visibility. Throws to fail.</summary>
    public Func<int, bool, WorkflowHubUpload> Respond { get; set; } = (n, makePublic) =>
        new WorkflowHubUpload(
            41 + n, $"https://dev.workflowhub.eu/workflows/{41 + n}?version=1", 1, makePublic,
            $"{{\"raw\":{n}}}", $"{{\"policy\":{n}}}", $"{{\"policyResponse\":{n}}}");

    /// <summary>Awaited by <see cref="UploadAsync"/> before it records the call, so a test can hold an upload open.</summary>
    public Func<Task>? BeforeUpload { get; set; }

    /// <summary>
    /// Awaited by <see cref="ExchangeCodeAsync"/> with the token it received, before it hands out the
    /// access token. A test holds the exchange open with it and can see whether the caller passed a
    /// cancellable token on.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeExchange { get; set; }

    public List<(string BaseUrl, string Token, byte[] Crate, int TeamId, bool MakePublic)> Uploads { get; } = [];
    public List<(string BaseUrl, string Token)> TeamLists { get; } = [];
    public List<(string BaseUrl, string ClientId, string Code, string RedirectUri, string CodeVerifier)> Exchanges { get; } = [];
    public List<(string BaseUrl, string ClientId, string AccessToken)> Revokes { get; } = [];

    // Concurrent requests share one fake, so writes to the recorded lists take this lock.
    private readonly object _recorded = new();

    public async Task<string> ExchangeCodeAsync(
        string baseUrl, string clientId, string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        lock (_recorded) Exchanges.Add((baseUrl, clientId, code, redirectUri, codeVerifier));
        if (BeforeExchange is { } before) await before(ct);
        return ExchangeFails is null ? AccessToken : throw ExchangeFails;
    }

    public Task<IReadOnlyList<WorkflowHubTeam>> ListTeamsAsync(string baseUrl, string token, CancellationToken ct)
    {
        lock (_recorded) TeamLists.Add((baseUrl, token));
        return ListFails is null
            ? Task.FromResult(Teams)
            : Task.FromException<IReadOnlyList<WorkflowHubTeam>>(ListFails);
    }

    public async Task<WorkflowHubUpload> UploadAsync(
        string baseUrl, string token, byte[] crate, int teamId, bool makePublic, CancellationToken ct)
    {
        if (BeforeUpload is { } before) await before();

        int call;
        lock (_recorded)
        {
            call = Uploads.Count;
            Uploads.Add((baseUrl, token, crate, teamId, makePublic));
        }

        return Respond(call, makePublic);
    }

    public Task RevokeAsync(string baseUrl, string clientId, string accessToken, CancellationToken ct)
    {
        lock (_recorded) Revokes.Add((baseUrl, clientId, accessToken));
        return RevokeFails is null ? Task.CompletedTask : Task.FromException(RevokeFails);
    }
}

/// <summary>Keeps every log line, so tests can assert what reached the log.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception) + exception);
}

/// <summary>Collects every log line of the app, so a test can assert what reached the log.</summary>
internal sealed class CapturingLoggerProvider(List<string> lines) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (lines) lines.Add($"{category}: {formatter(state, exception)}{exception}");
        }
    }
}

internal static class StoredStrings
{
    /// <summary>
    /// Checks every string column of every table, so a new field cannot quietly start carrying a secret.
    /// </summary>
    public static async Task AssertNoneContain(SubmissionDbContext db, string secret)
    {
        IEnumerable<object> everything =
        [
            .. await db.Submissions.AsNoTracking().ToListAsync(),
            .. await db.SubmissionAttempts.AsNoTracking().ToListAsync(),
            .. await db.SubmissionPayloads.AsNoTracking().ToListAsync(),
            .. await db.SubmissionReceipts.AsNoTracking().ToListAsync(),
            .. await db.Accessions.AsNoTracking().ToListAsync(),
            .. await db.SubmissionEvents.AsNoTracking().ToListAsync(),
            .. await db.PreparedArtifacts.AsNoTracking().ToListAsync(),
            .. await db.Notifications.AsNoTracking().ToListAsync()
        ];

        foreach (var entity in everything)
        {
            var strings = entity.GetType().GetProperties()
                .Where(p => p.PropertyType == typeof(string))
                .Select(p => (string?)p.GetValue(entity));
            Assert.All(strings, s => Assert.DoesNotContain(secret, s ?? ""));
        }
    }
}

/// <summary>The real app with PostgreSQL swapped for InMemory and the WorkflowHub edges faked.</summary>
internal sealed class WorkflowHubAppFactory(
    FakeWorkflowHubGateway gateway, bool allowProduction = false, string sandboxClientId = "test-client",
    string publicBaseUrl = "http://localhost:8081", string productionClientId = "test-client")
    : WebApplicationFactory<Program>
{
    private readonly string _database = Guid.NewGuid().ToString();

    /// <summary>Every log line the app wrote.</summary>
    public List<string> Logs { get; } = [];

    private sealed class UnusedPacker : IWorkflowCratePacker
    {
        public Task<WorkflowPreparation> PrepareAsync(
            Uri dataHubInstance, int projectId, string commitSha, string token, CancellationToken ct)
            => throw new InvalidOperationException("These tests call no hook.");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // The health check registration rejects an empty connection string. Nothing connects to it.
        builder.UseSetting("PostgresConnectionString", "Host=localhost;Database=unused");
        builder.UseSetting("EndpointSubmission:PublicBaseUrl", publicBaseUrl);
        builder.UseSetting("EndpointSubmission:AllowProductionTarget", allowProduction ? "true" : "false");
        builder.UseSetting("WorkflowHub:Sandbox:ClientId", sandboxClientId);
        builder.UseSetting("WorkflowHub:Production:ClientId", productionClientId);
        builder.ConfigureLogging(logging => logging.AddProvider(new CapturingLoggerProvider(Logs)));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<SubmissionDbContext>>();
            services.AddDbContext<SubmissionDbContext>(o => o.UseInMemoryDatabase(_database));
            services.RemoveAll<IWorkflowCratePacker>();
            services.AddSingleton<IWorkflowCratePacker, UnusedPacker>();
            services.RemoveAll<IWorkflowHubGateway>();
            services.AddSingleton<IWorkflowHubGateway>(gateway);
        });
    }
}
