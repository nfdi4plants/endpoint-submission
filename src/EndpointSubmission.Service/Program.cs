using EndpointSubmission.Service.API;
using EndpointSubmission.Service.Authentication;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.ENA;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Services;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Pages are plain interpolated HTML returned from minimal-API handlers (see Pages/), the way
// AVPR does it — no Razor, no MVC, so nothing to register here beyond the API Explorer NSwag
// needs to discover the endpoints.
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddOpenApiDocument(config =>
{
    config.Title = "ARC Endpoint Submission";
    config.Description = "CQC hook endpoints for submitting ARC contents to endpoint repositories.";
});

builder.Services.Configure<EndpointSubmissionOptions>(
    builder.Configuration.GetSection(EndpointSubmissionOptions.SectionName));

builder.Services.AddOptions<WorkflowHubOptions>()
    .Bind(builder.Configuration.GetSection(WorkflowHubOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<KeyedAsyncLock>();

// The WorkflowHub login cookie is protected with an in-memory key ring. Nothing reaches the disk,
// and a restart invalidates the logins that are in progress.
builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
builder.Services.AddSingleton<WorkflowHubFlowCookie>();

var connectionString = builder.Configuration.GetValue<string>("PostgresConnectionString");
builder.Services.AddDbContext<SubmissionDbContext>(options => options.UseNpgsql(connectionString));

// Reads the DataHUB through the DataHubClient library, which brings its own transport.
builder.Services.AddSingleton<IArcMetadataReader, ArcMetadataReader>();

// Checks the ARC out with git and packs the crates, with the PAT of the hook request.
builder.Services.AddSingleton<IWorkflowCratePacker, WfHubCratePacker>();

// Talks to WorkflowHub with the OAuth access token of one request.
builder.Services.AddSingleton<IWorkflowHubGateway, WfHubGateway>();

builder.Services.AddScoped<WorkflowHubUploadService>();

builder.Services.AddScoped<SubmissionIntakeService>();
builder.Services.AddScoped<SubmissionPageAccess>();
builder.Services.AddScoped<EnaSubmissionHandler>();
builder.Services.AddScoped<WorkflowHubSubmissionHandler>();
builder.Services.AddSingleton<DataHubUrlGuard>();
builder.Services.AddSingleton<StatusTokenService>();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IHookAuthenticator, DevelopmentHookAuthenticator>();
}
else
{
    builder.Services.AddSingleton<IHookAuthenticator, SharedSecretHookAuthenticator>();
}

builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString ?? string.Empty, name: "submissions-db");

var app = builder.Build();

// A crash or restart can leave ARC checkouts, private repository content, in the temp folder.
// This process has started no checkout yet, so a folder older than one preparation timeout is
// a leftover. Tests start apps beside other processes that use the same temp folder, so they skip it.
if (!app.Environment.IsEnvironment("Testing"))
{
    var preparationTimeout = TimeSpan.FromSeconds(
        app.Services.GetRequiredService<IOptions<WorkflowHubOptions>>().Value.PreparationTimeoutSeconds);
    var sweptCheckouts = DataHubGit.Checkout.sweepStaleRoots(preparationTimeout);
    if (sweptCheckouts > 0)
        app.Logger.LogInformation("Removed {Count} stale ARC checkout folders", sweptCheckouts);
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();

    // Dev only. In production, migrations are applied deliberately as a deploy step —
    // this database is the sole record that a submission happened, so schema changes
    // should not ride in silently on an app restart.
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<SubmissionDbContext>().Database.Migrate();
}
else
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseOpenApi();
app.UseSwaggerUi();

app.UseRouting();

app.MapGroup("/api/v1/hooks")
   .MapHookApi()
   .WithTags("Hooks");

app.MapGroup("/")
   .MapPageEndpoints();

app.MapHealthChecks("/_health", new HealthCheckOptions
{
    ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
});

app.Run();

/// <summary>Exposed so integration tests can drive the app through WebApplicationFactory.</summary>
public partial class Program { }
