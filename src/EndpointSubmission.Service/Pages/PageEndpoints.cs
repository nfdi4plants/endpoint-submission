using EndpointSubmission.Service.Pages.Handlers;

namespace EndpointSubmission.Service.Pages;

/// <summary>
/// Server-rendered pages, mapped as minimal-API routes exactly as AVPR does — no Razor, no MVC.
/// The submission preview and status pages join this list in M2.
/// </summary>
public static class PageEndpoints
{
    public static RouteGroupBuilder MapPageEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("", IndexHandlers.Render).ExcludeFromDescription();

        group.MapGet("error", ErrorHandlers.Render).ExcludeFromDescription();

        return group;
    }
}
