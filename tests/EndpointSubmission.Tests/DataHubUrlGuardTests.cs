using EndpointSubmission.Service.Services;
using Microsoft.Extensions.Options;

namespace EndpointSubmission.Tests;

public class DataHubUrlGuardTests
{
    private const string ValidUrl =
        "https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/isa_arc_json/0.0.1/arc-ro-crate-metadata.json";

    private static DataHubUrlGuard Guard(params string[] hosts) =>
        new(Options.Create(new EndpointSubmissionOptions { AllowedDataHubHosts = hosts }));

    private static DataHubUrlGuard Default() => Guard("gitdev.nfdi4plants.org");

    [Fact]
    public void Resolves_a_valid_generic_package_url_into_its_components()
    {
        var ok = Default().TryResolve(ValidUrl, expectedProjectId: 2, out var location, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("gitdev.nfdi4plants.org", location!.Instance.Host);
        Assert.Equal("https://gitdev.nfdi4plants.org", location.Instance.ToString().TrimEnd('/'));
        Assert.Equal(2, location.ProjectId);
        Assert.Equal("isa_arc_json", location.PackageName);
        Assert.Equal("0.0.1", location.Version);
        Assert.Equal("arc-ro-crate-metadata.json", location.FileName);
    }

    [Fact]
    public void Rejects_host_outside_allowlist()
    {
        var ok = Default().TryResolve(
            "https://attacker.example/api/v4/projects/2/packages/generic/n/1/f.json", 2, out _, out var error);

        Assert.False(ok);
        Assert.Contains("not an allowed DataHUB host", error);
    }

    [Theory]
    [InlineData("http://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/n/1/f.json")] // token in plaintext
    [InlineData("ftp://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/n/1/f.json")]
    [InlineData("file:///etc/passwd")]
    public void Rejects_non_https_schemes(string url)
    {
        Assert.False(Default().TryResolve(url, 2, out _, out var error));
        Assert.Contains("https", error);
    }

    [Fact]
    public void Rejects_internal_addresses_even_when_reachable()
    {
        // The point of the allowlist: an unchecked ro_crate_url is an authenticated request
        // aimed wherever the caller likes, including inside the deployment.
        Assert.False(Default().TryResolve("https://169.254.169.254/latest/meta-data/", 2, out _, out _));
        Assert.False(Default().TryResolve("https://localhost:5432/", 2, out _, out _));
    }

    [Fact]
    public void Empty_allowlist_fetches_nothing()
    {
        // The correct failure mode for a misconfigured deployment.
        Assert.False(Guard().TryResolve(ValidUrl, 2, out _, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("/relative/path")]
    public void Rejects_missing_or_unparseable_urls(string? url)
    {
        Assert.False(Default().TryResolve(url, 2, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Host_comparison_is_case_insensitive()
    {
        Assert.True(Guard("GitDev.NFDI4Plants.org").TryResolve(ValidUrl, 2, out _, out _));
    }

    [Theory]
    // Right host, but not a generic package file — the library fetches by component, so a URL
    // it cannot be decomposed into is not usable regardless of how trusted the host is.
    [InlineData("https://gitdev.nfdi4plants.org/api/v4/projects/2/repository/files/isa.investigation.xlsx/raw")]
    [InlineData("https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/maven/n/1/f.jar")]
    [InlineData("https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/n/1")]
    [InlineData("https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/n/1/f/extra")]
    [InlineData("https://gitdev.nfdi4plants.org/")]
    public void Rejects_urls_that_are_not_generic_package_files(string url)
    {
        Assert.False(Default().TryResolve(url, 2, out _, out var error));
        Assert.Contains("generic package file URL", error);
    }

    [Fact]
    public void Rejects_a_non_numeric_project_id()
    {
        Assert.False(Default().TryResolve(
            "https://gitdev.nfdi4plants.org/api/v4/projects/mine/packages/generic/n/1/f.json", 2, out _, out var error));
        Assert.Contains("numeric project id", error);
    }

    [Fact]
    public void Rejects_a_project_id_that_disagrees_with_the_hook()
    {
        var ok = Default().TryResolve(ValidUrl, expectedProjectId: 99, out _, out var error);

        Assert.False(ok);
        Assert.Contains("project 2", error);
        Assert.Contains("project 99", error);
    }

    [Fact]
    public void Unescapes_path_segments()
    {
        var ok = Default().TryResolve(
            "https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/isa%20arc/0.0.1/arc%2Bmeta.json",
            2, out var location, out _);

        Assert.True(ok);
        Assert.Equal("isa arc", location!.PackageName);
        Assert.Equal("arc+meta.json", location.FileName);
    }
}
