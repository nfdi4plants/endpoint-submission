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

        group.MapGet("workflowhub/submissions/{id:guid}", WorkflowHubPreviewHandlers.Render)
            .ExcludeFromDescription();

        return group;
    }
}
