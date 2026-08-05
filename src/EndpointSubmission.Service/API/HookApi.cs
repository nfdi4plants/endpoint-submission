using EndpointSubmission.Service.Authentication;
using EndpointSubmission.Service.Models;
using EndpointSubmission.Service.Services;

namespace EndpointSubmission.Service.API;

/// <summary>
/// CQC hook endpoints. One route per endpoint repository; ENA is the only one for now,
/// but the shape is per-endpoint so /pride can be added without disturbing it.
/// </summary>
public static class HookApi
{
    public static RouteGroupBuilder MapHookApi(this RouteGroupBuilder group)
    {
        group.MapPost("/ena", async (
            HookRequest request,
            HttpRequest http,
            HookAuthenticator auth,
            SubmissionIntakeService intake,
            CancellationToken ct) =>
        {
            if (!auth.IsConfigured)
            {
                // Refuse rather than fall open. A deployment with no hook secret is
                // misconfigured, and this endpoint is not safe to leave unauthenticated.
                return Results.Problem(
                    "Hook authentication is not configured on this deployment.",
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (!auth.IsAuthentic(http))
                return Results.Unauthorized();

            var result = await intake.IntakeAsync(request, endpoint: "ena", ct);

            return result switch
            {
                IntakeResult.Accepted a => Results.Ok(new HookResponse(a.FollowMe)),
                IntakeResult.Rejected r => Results.BadRequest(new { error = r.Reason }),
                IntakeResult.Unauthorized u => Results.Json(new { error = u.Reason }, statusCode: StatusCodes.Status401Unauthorized),
                IntakeResult.Upstream up => Results.Json(new { error = up.Reason }, statusCode: StatusCodes.Status502BadGateway),
                _ => Results.Problem("Unhandled intake result.")
            };
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

        return group;
    }
}
