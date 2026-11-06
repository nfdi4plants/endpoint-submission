using EndpointSubmission.Service.API;
using EndpointSubmission.Service.Authentication;
using EndpointSubmission.Service.Data;
using EndpointSubmission.Service.Endpoints.ENA;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Pages;
using EndpointSubmission.Service.Services;
using HealthChecks.UI.Client;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

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

var connectionString = builder.Configuration.GetValue<string>("PostgresConnectionString");
builder.Services.AddDbContext<SubmissionDbContext>(options => options.UseNpgsql(connectionString));

// Reads the DataHUB through the DataHubClient library, which brings its own transport.
builder.Services.AddSingleton<IArcMetadataReader, ArcMetadataReader>();

// Placeholder until the real packer is registered here.
builder.Services.AddSingleton<IWorkflowCratePacker, UnavailableWorkflowCratePacker>();

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
