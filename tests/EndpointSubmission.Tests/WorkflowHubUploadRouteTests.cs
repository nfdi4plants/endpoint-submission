using System.Net;

namespace EndpointSubmission.Tests;

public class WorkflowHubUploadRouteTests
{
    [Fact]
    public async Task The_page_assets_are_served_and_the_script_makes_no_requests()
    {
        using var factory = new WorkflowHubAppFactory(new FakeWorkflowHubGateway());
        using var client = factory.CreateClient();

        var css = await client.GetAsync("/css/workflowhub.css");
        var stylesheet = await css.Content.ReadAsStringAsync();
        var js = await client.GetAsync("/js/workflowhub.js");
        var script = await js.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".wfh-notice", stylesheet);
        Assert.Contains("overflow-wrap: anywhere", stylesheet);
        Assert.DoesNotContain("http", stylesheet);
        Assert.Equal(HttpStatusCode.OK, js.StatusCode);
        Assert.Contains("aria-busy", script);
        Assert.Contains("data-wfh-select-all", script);
        Assert.DoesNotContain("fetch(", script);
        Assert.DoesNotContain("XMLHttpRequest", script);
    }
}
