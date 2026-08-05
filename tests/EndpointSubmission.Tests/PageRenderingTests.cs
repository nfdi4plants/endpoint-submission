using EndpointSubmission.Service.Pages.Components;
using EndpointSubmission.Service.Pages.Handlers;

namespace EndpointSubmission.Tests;

public class PageRenderingTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;")]
    [InlineData("Tom & Jerry", "Tom &amp; Jerry")]
    [InlineData("\" onload=\"evil()", "&quot;")]
    public void Html_Text_escapes_markup(string input, string expectedFragment)
    {
        var encoded = Html.Text(input);

        Assert.Contains(expectedFragment, encoded);
        Assert.DoesNotContain("<script>", encoded);
    }

    [Fact]
    public void Html_Text_treats_null_as_empty()
        => Assert.Equal(string.Empty, Html.Text(null));

    [Fact]
    public void Layout_escapes_the_title()
    {
        // Titles will carry ARC project names, which come from the DataHUB.
        var html = Layout.Render(title: "<img src=x onerror=alert(1)>", content: "<p>ok</p>");

        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img src=x", html);
    }

    [Fact]
    public void Layout_serves_picocss_from_wwwroot_not_a_cdn()
    {
        // A CDN link would be an external dependency on a page users hit mid-submission.
        var html = Layout.Render("t", "<p>c</p>");

        Assert.Contains(@"href=""/css/pico.cyan.min.css""", html);
        Assert.DoesNotContain("cdn.", html);
        Assert.DoesNotContain("unpkg", html);
    }

    [Fact]
    public void Layout_suppresses_the_referrer()
    {
        // Status page URLs carry the access token in the query string; a Referer header would
        // hand it to anything those pages ever link out to.
        Assert.Contains(@"name=""referrer"" content=""no-referrer""", Layout.Render("t", "c"));
    }

    [Fact]
    public void Layout_follows_the_system_colour_scheme()
        => Assert.Contains(@"content=""light dark""", Layout.Render("t", "c"));

    [Fact]
    public void Layout_emits_content_unescaped_so_components_compose()
    {
        var html = Layout.Render("t", "<p>literal <strong>markup</strong></p>");

        Assert.Contains("<p>literal <strong>markup</strong></p>", html);
    }

    [Fact]
    public void Index_handler_returns_html()
    {
        var result = IndexHandlers.Render();

        Assert.Equal("text/html", result.ContentType);
        Assert.Contains("<!DOCTYPE html>", result.ResponseContent);
        Assert.Contains("ARC Endpoint Submission", result.ResponseContent);
    }

    [Fact]
    public void Error_handler_reveals_nothing_about_the_failure()
    {
        var result = ErrorHandlers.Render();

        Assert.Equal("text/html", result.ContentType);
        Assert.DoesNotContain("Exception", result.ResponseContent);
        Assert.DoesNotContain("Stack", result.ResponseContent);
    }
}
