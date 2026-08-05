using EndpointSubmission.Service.Authentication;
using EndpointSubmission.Service.Endpoints.ENA;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;

namespace EndpointSubmission.Service.API;

/// <summary>
/// CQC hook endpoints. Routes are explicit because every endpoint owns its package payload,
/// preparation, and preview. Only authenticated envelope ingestion and persistence are shared.
/// </summary>
public static class HookApi
{
    public static RouteGroupBuilder MapHookApi(this RouteGroupBuilder group)
    {
        group.MapPost("/ena", async (
            HookRequest request,
            HttpRequest http,
            HookAuthenticator auth,
            EnaSubmissionHandler handler,
            CancellationToken ct) =>
        {
            var authenticationFailure = Authenticate(http, auth);
            if (authenticationFailure is not null) return authenticationFailure;

            return ToHttpResult(await handler.HandleAsync(request, ct));
        })
        .WithName("EnaHook")
        .WithSummary("CQC hook endpoint for ENA submission.")
        .WithDescription(
            "Invoked by AaaS when a user clicks the ENA validation package badge on an ARC. " +
            "Returns the URL the user's browser should follow.")
        .Produces<HookResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status502BadGateway);

        group.MapPost("/workflowhub", async (
            HookRequest request,
            HttpRequest http,
            HookAuthenticator auth,
            WorkflowHubSubmissionHandler handler,
            CancellationToken ct) =>
        {
            var authenticationFailure = Authenticate(http, auth);
            if (authenticationFailure is not null) return authenticationFailure;

            return ToHttpResult(await handler.HandleAsync(request, ct));
        })
        .WithName("WorkflowHubHook")
        .WithSummary("CQC hook endpoint for WorkflowHub submission.")
        .WithDescription(
            "Invoked when a user clicks a WorkflowHub validation package badge on an ARC. " +
            "Returns the endpoint-specific preview URL the browser should follow.")
        .Produces<HookResponse>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status502BadGateway);

        return group;
    }

    private static IResult? Authenticate(HttpRequest request, HookAuthenticator auth)
    {
        if (!auth.IsConfigured)
        {
            return Results.Problem(
                "Hook authentication is not configured on this deployment.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return auth.IsAuthentic(request) ? null : Results.Unauthorized();
    }

    private static IResult ToHttpResult(IntakeResult result) => result switch
    {
        IntakeResult.Accepted accepted => Results.Ok(new HookResponse(accepted.FollowMe)),
        IntakeResult.Rejected rejected => Results.BadRequest(new { error = rejected.Reason }),
        IntakeResult.Unauthorized unauthorized => Results.Json(
            new { error = unauthorized.Reason }, statusCode: StatusCodes.Status401Unauthorized),
        IntakeResult.Upstream upstream => Results.Json(
            new { error = upstream.Reason }, statusCode: StatusCodes.Status502BadGateway),
        _ => Results.Problem("Unhandled intake result.")
    };
}
