# DataHubGit

DataHubGit gives .NET code git access to a DataHUB, the GitLab instance that hosts ARCs. It makes a
shallow checkout of one commit of a DataHUB project into a temporary folder and hydrates the Git LFS
files under the paths you select. Apart from FSharp.Core, the library has no package dependencies.
It starts the `git` and `git-lfs` programs from `PATH`.

## Build and test

Build once with `dotnet build src/DataHubGit/DataHubGit.fsproj`. Another project in this repository
uses the library through a project reference:

```powershell
dotnet add <your project> reference src/DataHubGit/DataHubGit.fsproj
```

Run the tests with `dotnet test tests/DataHubGit.Tests/DataHubGit.Tests.fsproj`. They build local git
repositories in temporary folders and need git and git-lfs on `PATH`. See the tests README.

## Checking out an ARC

`Checkout.checkout` fetches one commit of a DataHUB project into a temporary folder with git. It
takes the DataHUB host (for example `gitdev.nfdi4plants.org`), the numeric project id, a GitLab
personal access token, a commit id (null or empty means the default branch head), the LFS paths
to hydrate (null or empty means none), a timeout for the whole call and a cancellation token.
A pattern with a comma throws `ArgumentException`, and a negative timeout other than
`Timeout.InfiniteTimeSpan` throws `ArgumentOutOfRangeException`. Both happen before any work starts.

The checkout is shallow and holds exactly the requested commit. Git LFS files stay pointer
text, except those under the paths you pass, for example `workflows/**`. Everything outside the
hydrated paths stays a pointer, which keeps the checkout small.

git-lfs reads `.lfsconfig` from the ARC, so an ARC can name its own LFS server. Every git-lfs
call of the checkout therefore sets `lfs.url` to the endpoint of the DataHUB clone URL and clears
`lfs.fetchexclude` on the command line, which beats the file.

The returned `ArcCheckout` has `Directory`, the ARC root, and `CommitSha`. `IsShallow` is always
true. Dispose it to delete the temporary folder, which also holds the isolated HOME and git
config that the calls used. A failed or cancelled checkout tries to delete the folder before the
exception leaves. Deletion is best effort, so a folder that cannot be removed stays behind.
`Checkout.sweepStaleRoots` removes such leftovers. It deletes the `datahubgit-checkout-*` folders in
the temp directory that are older than a given age and returns their count, so call it at
startup with an age above your checkout timeout.

Failures raise `CheckoutException` with a typed `Error` (`Unauthorized`, `NotFound`, `Network`,
`TimedOut`, `GitMissing`, `GitFailed` or `LfsFailed`), and cancelling the token raises
`OperationCanceledException`.

git 2.31 or later and git-lfs must be on `PATH`. The checkout tests ran with git 2.51 and git-lfs
3.7.0. The service image runs git 2.39.5 and git-lfs 3.3.0, and the DataHubGit tests pass there.
The git processes see an allowlist of environment variables. It includes `HTTPS_PROXY`,
`HTTP_PROXY`, `NO_PROXY` (upper and lower case), `SSL_CERT_FILE` and `SSL_CERT_DIR` when they are
set, so a proxy or a custom CA bundle keeps working.

A checkout writes each committed symbolic link as a plain file that holds the link target as text,
because the ARC is untrusted and a link could point at any file of the host.

The token is passed per call and never stored. It reaches git through the environment of the two
git calls that talk to DataHUB, the fetch and the LFS download. There it is a `GIT_CONFIG_*`
setting for `http.https://<host>/.extraHeader`, scoped to the DataHUB host. Those git processes
and their children (git-remote-https and git-lfs) can read it. It is never in argv, the clone
URL, `.git/config` or the temporary folder. Error details come from redacted git output.

Save this as `checkout.fsx` in the repository root (the `#r` path resolves relative to the
script) and run `dotnet fsi checkout.fsx`:

```fsharp
#r "src/DataHubGit/bin/Debug/net8.0/DataHubGit.dll"
open System
open System.Threading
open DataHubGit

use checkout =
    Checkout
        .checkout(
            "gitdev.nfdi4plants.org",
            1234,
            "<personal access token>",
            null,
            [ "workflows/**" ],
            TimeSpan.FromMinutes 5.,
            CancellationToken.None
        )
        .GetAwaiter()
        .GetResult()

printfn "%s is at commit %s" checkout.Directory checkout.CommitSha
```

## Layout

| Files | Contents |
|---|---|
| `GitProcess.fs` | the git process runner, child environment, auth header and redaction helpers (internal) |
| `Checkout.fs` | `Checkout.checkout`, `Checkout.sweepStaleRoots`, `ArcCheckout`, `CheckoutError` and `CheckoutException` |
