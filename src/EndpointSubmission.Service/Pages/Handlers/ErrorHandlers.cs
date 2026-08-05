using Microsoft.AspNetCore.Http.HttpResults;
using EndpointSubmission.Service.Pages.Components;

namespace EndpointSubmission.Service.Pages.Handlers;

public static class ErrorHandlers
{
    /// <summary>
    /// Target of <c>UseExceptionHandler</c>. Says nothing about what failed: this service handles
    /// credentials and private ARC metadata, so exception detail belongs in the log, not on a page
    /// that may be sitting on a shared screen.
    /// </summary>
    public static ContentHttpResult Render()
    {
        var content = Layout.Render(
            title: "Something went wrong",
            content: @"
              <hgroup>
                <h1>Something went wrong</h1>
                <p>The request could not be completed.</p>
              </hgroup>
              <p>
                Nothing was submitted to an endpoint repository as a result of this error. If you
                arrived here from a badge, clicking it again is safe — it reopens the same
                submission rather than creating a second one.
              </p>");

        return TypedResults.Text(content: content, contentType: "text/html");
    }
}
