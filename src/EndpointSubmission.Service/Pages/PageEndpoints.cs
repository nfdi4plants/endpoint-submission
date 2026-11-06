using EndpointSubmission.Service.Pages.Handlers;
using EndpointSubmission.Service.Endpoints.ENA;
using EndpointSubmission.Service.Endpoints.WorkflowHub;

namespace EndpointSubmission.Service.Pages;

/// <summary>
/// Server-rendered pages, mapped as minimal-API routes exactly as AVPR does — no Razor, no MVC.
/// Each endpoint owns its preview route and rendering; only token access and the HTML shell are shared.
/// </summary>
public static class PageEndpoints
{
    public static RouteGroupBuilder MapPageEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("", IndexHandlers.Render).ExcludeFromDescription();

        group.MapGet("error", ErrorHandlers.Render).ExcludeFromDescription();

        group.MapGet("ena/submissions/{id:guid}", EnaPreviewHandlers.Render)
            .ExcludeFromDescription();

        // These pages carry the status token and the login of an upload, so browsers and proxies
        // must not keep copies.
        group.MapGet("workflowhub/submissions/{id:guid}", WorkflowHubPreviewHandlers.Render)
            .ExcludeFromDescription()
            .NoStore();

        group.MapPost("workflowhub/submissions/{id:guid}/authorize", WorkflowHubUploadHandlers.Authorize)
            .ExcludeFromDescription()
            .NoStore();

        group.MapGet("workflowhub/oauth/callback", WorkflowHubUploadHandlers.Callback)
            .ExcludeFromDescription()
            .NoStore();

        return group;
    }

    private static RouteHandlerBuilder NoStore(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
}
