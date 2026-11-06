using DataHubGit;
using EndpointSubmission.Service.Endpoints.WorkflowHub;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;
using WfHubClient;

namespace EndpointSubmission.Tests;

public class WfHubCratePackerTests
{
    public static IEnumerable<object[]> CheckoutFailures() =>
    [
        [CheckoutError.NewUnauthorized("HTTP 401"), "The DataHUB refused the token while checking the ARC out."],
        [CheckoutError.NewNotFound("Repository not found"), "The ARC or its commit was not found on the DataHUB. Repository not found"],
        [CheckoutError.NewNetwork("no route"), "The DataHUB could not be reached."],
        [CheckoutError.TimedOut, "Checking the ARC out took too long."],
        [CheckoutError.NewGitMissing("not installed"), "git or git-lfs is missing on the server."],
        [CheckoutError.NewGitFailed("fatal: bad object"), "Checking the ARC out failed. fatal: bad object"],
        [CheckoutError.NewLfsFailed("smudge failed"), "Checking the ARC out failed. smudge failed"]
    ];

    [Theory]
    [MemberData(nameof(CheckoutFailures))]
    public void A_checkout_failure_becomes_a_preparation_error_with_a_fixed_lead(CheckoutError error, string expected)
    {
        var preparation = WfHubCratePacker.MapCheckoutFailure(error);

        Assert.Equal(expected, preparation.Error);
        Assert.Empty(preparation.Items);
        Assert.Null(preparation.LicenseSpdxId);
        Assert.False(preparation.LicensePresent);
    }

    private static PackedItem Item(string id, ItemKind kind, string? title, ItemStatus status) =>
        new(id, kind, title is null ? FSharpOption<string>.None : FSharpOption<string>.Some(title),
            FSharpOption<string>.None, status);

    private static FSharpList<string> Strings(params string[] values) => ListModule.OfArray(values);

    [Fact]
    public void A_pack_result_maps_each_item_status_and_the_license()
    {
        byte[] crate = [1, 2, 3];
        var packed = new PackResult(
            ListModule.OfArray(new[]
            {
                Item("workflows/align", ItemKind.Workflow, "Align reads", ItemStatus.NewUploadable(crate, 3L)),
                Item("runs/r1", ItemKind.Run, null, ItemStatus.NewNotReady(Strings("No outputs.", "No inputs."))),
                Item("runs/r2", ItemKind.Run, null, ItemStatus.NewPackFailed("Zip assembly failed."))
            }),
            FSharpOption<string>.Some("MIT License text"),
            FSharpOption<string>.Some("MIT"),
            FSharpOption<string>.None);

        var preparation = WfHubCratePacker.MapPackResult(packed);

        Assert.Null(preparation.Error);
        Assert.Equal("MIT", preparation.LicenseSpdxId);
        Assert.True(preparation.LicensePresent);

        var uploadable = preparation.Items[0];
        Assert.Equal(("workflow", "workflows/align", "Align reads"), (uploadable.Kind, uploadable.Identifier, uploadable.Title));
        Assert.Equal(PreparedWorkflowItemStatus.Uploadable, uploadable.Status);
        Assert.Equal(crate, uploadable.Crate);
        Assert.Empty(uploadable.Failures);

        var notReady = preparation.Items[1];
        Assert.Equal("run", notReady.Kind);
        Assert.Equal(PreparedWorkflowItemStatus.NotReady, notReady.Status);
        Assert.Equal(new[] { "No outputs.", "No inputs." }, notReady.Failures);
        Assert.Null(notReady.Crate);

        var packFailed = preparation.Items[2];
        Assert.Equal(PreparedWorkflowItemStatus.PackFailed, packFailed.Status);
        Assert.Equal(new[] { "Zip assembly failed." }, packFailed.Failures);
        Assert.Null(packFailed.Crate);
    }

    [Fact]
    public void A_pack_result_without_a_license_file_reports_it()
    {
        var packed = new PackResult(
            ListModule.OfArray(new[] { Item("runs/r1", ItemKind.Run, null, ItemStatus.NewNotReady(Strings("No license."))) }),
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<string>.None);

        var preparation = WfHubCratePacker.MapPackResult(packed);

        Assert.False(preparation.LicensePresent);
        Assert.Null(preparation.LicenseSpdxId);
        Assert.Single(preparation.Items);
    }

    [Fact]
    public void An_ARC_that_cannot_be_loaded_becomes_a_preparation_error()
    {
        var packed = new PackResult(
            FSharpList<PackedItem>.Empty,
            FSharpOption<string>.None,
            FSharpOption<string>.None,
            FSharpOption<string>.Some("arc.yml is not valid"));

        var preparation = WfHubCratePacker.MapPackResult(packed);

        Assert.Equal("The ARC could not be loaded: arc.yml is not valid", preparation.Error);
        Assert.Empty(preparation.Items);
    }
}
