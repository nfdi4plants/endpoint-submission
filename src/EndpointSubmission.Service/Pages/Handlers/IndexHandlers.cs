using Microsoft.AspNetCore.Http.HttpResults;
using EndpointSubmission.Service.Pages.Components;

namespace EndpointSubmission.Service.Pages.Handlers;

public static class IndexHandlers
{
    public static ContentHttpResult Render()
    {
        var content = Layout.Render(
            title: "ARC Endpoint Submission",
            content: Components.Index.Render());

        return TypedResults.Text(content: content, contentType: "text/html");
    }
}
