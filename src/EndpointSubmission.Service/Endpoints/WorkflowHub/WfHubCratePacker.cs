using System.Runtime.CompilerServices;
using DataHubGit;
using EndpointSubmission.Service.Services;
using Microsoft.Extensions.Options;
using Microsoft.FSharp.Core;
using WfHubClient;

[assembly: InternalsVisibleTo("EndpointSubmission.Tests")]

namespace EndpointSubmission.Service.Endpoints.WorkflowHub;

/// <summary>
/// <see cref="IWorkflowCratePacker"/> over two libraries. DataHubGit checks the ARC commit out of the
/// DataHUB with git, WfHubClient packs every workflow and run, and this class deletes the checkout
/// afterwards. The libraries' F# types stay inside this class.
///
/// The token goes to the checkout as an argument and is gone when the call returns. Nothing in the
/// returned <see cref="WorkflowPreparation"/> comes from the token, and DataHubGit redacts git output.
/// </summary>
public sealed class WfHubCratePacker(IOptions<WorkflowHubOptions> options) : IWorkflowCratePacker
{
    public async Task<WorkflowPreparation> PrepareAsync(
        Uri dataHubInstance, int projectId, string commitSha, string token, CancellationToken ct)
    {
        ArcCheckout checkout;
        try
        {
            checkout = await Checkout.checkout(
                dataHubInstance.Authority,
                projectId,
                token,
                commitSha,
                Pack.WorkflowHubLfsInclude,
                TimeSpan.FromSeconds(options.Value.PreparationTimeoutSeconds),
                ct);
        }
        catch (CheckoutException ex)
        {
            return MapCheckoutFailure(ex.Error);
        }

        using (checkout)
        {
            // Loading the ARC and writing the crates is synchronous CPU and disk work.
            var packed = await Task.Run(() => Pack.pack(checkout.Directory), ct);
            return MapPackResult(packed);
        }
    }

    /// <summary>
    /// One fixed sentence per failure, followed by git's redacted detail where that detail helps
    /// the user (a missing project, a failed git or git-lfs run).
    /// </summary>
    internal static WorkflowPreparation MapCheckoutFailure(CheckoutError error)
    {
        var (lead, detail) = error switch
        {
            _ when error.IsUnauthorized => ("The DataHUB refused the token while checking the ARC out.", null),
            CheckoutError.NotFound notFound => ("The ARC or its commit was not found on the DataHUB.", notFound.detail),
            _ when error.IsNetwork => ("The DataHUB could not be reached.", null),
            _ when error.IsTimedOut => ("Checking the ARC out took too long.", null),
            _ when error.IsGitMissing => ("git or git-lfs is missing on the server.", null),
            CheckoutError.GitFailed gitFailed => ("Checking the ARC out failed.", gitFailed.detail),
            CheckoutError.LfsFailed lfsFailed => ("Checking the ARC out failed.", lfsFailed.detail),
            _ => ("Checking the ARC out failed.", null)
        };

        var message = string.IsNullOrWhiteSpace(detail) ? lead : $"{lead} {detail.Trim()}";
        return new WorkflowPreparation(Items: [], LicenseSpdxId: null, LicensePresent: false, Error: message);
    }

    internal static WorkflowPreparation MapPackResult(PackResult result)
    {
        var licensePresent = OptionModule.IsSome(result.License);

        var loadError = OptionModule.ToObj(result.ArcLoadError);
        if (loadError is not null)
        {
            return new WorkflowPreparation(
                Items: [],
                LicenseSpdxId: null,
                LicensePresent: licensePresent,
                Error: $"The ARC could not be loaded: {loadError}");
        }

        var items = result.Items.Select(MapItem).ToList();
        return new WorkflowPreparation(
            items, OptionModule.ToObj(result.LicenseSpdxId), licensePresent, Error: null);
    }

    private static PreparedWorkflowItem MapItem(PackedItem item)
    {
        var kind = item.Kind.IsRun ? "run" : "workflow";
        var title = OptionModule.ToObj(item.Title);
        var description = OptionModule.ToObj(item.Description);

        return item.Status switch
        {
            ItemStatus.Uploadable uploadable => new PreparedWorkflowItem(
                kind, item.Identifier, title, description,
                PreparedWorkflowItemStatus.Uploadable, [], uploadable.crate, uploadable.lfsPointers.ToList()),
            ItemStatus.NotReady notReady => new PreparedWorkflowItem(
                kind, item.Identifier, title, description,
                PreparedWorkflowItemStatus.NotReady, notReady.failures.ToList(), null, []),
            ItemStatus.PackFailed packFailed => new PreparedWorkflowItem(
                kind, item.Identifier, title, description,
                PreparedWorkflowItemStatus.PackFailed, [packFailed.error], null, []),
            _ => throw new InvalidOperationException("Unknown pack status.")
        };
    }
}
