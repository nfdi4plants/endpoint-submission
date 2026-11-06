# Test notes

Run the tests with `dotnet test tests/DataHubGit.Tests/DataHubGit.Tests.fsproj`.

## Checkout tests

`CheckoutTests.fs` builds local git repositories in temporary folders and checks out of them
over `file://`, so it needs git and git-lfs on `PATH` and no network. Fixture git calls set
the identity per call and ignore the global and system config. The tests cover:

- a shallow checkout at a commit id (files, `CommitSha`, `IsShallow`, `git rev-parse --is-shallow-repository`)
- a checkout without a commit id, which gives the latest commit
- `Dispose` deleting the whole temporary root
- an unknown commit id, which raises `CheckoutException`, leaves no folder behind and leaks no token form
- the temporary root of a credentialed checkout holding no token, base64 form or `extraHeader`
- the auth header key (lowercase host, port 443 dropped) and its Basic payload
- the fetch argv holding no auth pair and starting with an empty credential helper
- the header reaching git through `GIT_CONFIG_COUNT`, `GIT_CONFIG_KEY_0` and `GIT_CONFIG_VALUE_0`, and only when a token is given
- the child environment allowlist, with `GIT_TRACE` and `GIT_ASKPASS` set in the test process
- proxy and CA variables passing through when set and staying out when unset
- the `redact` table
- error classification of git and git-lfs output (`Unauthorized`, `NotFound`, `Network` and `GitFailed`)
- the LFS endpoint that git-lfs gets for each kind of remote
- clone URL validation (scheme, host, port, userinfo, query and fragment)
- input validation of host, token, project id and commit id, plus a comma in an LFS pattern and a negative timeout, which are rejected before any work
- an expired timeout, which reports `TimedOut` and deletes the temporary root
- a cancelled token, which raises `OperationCanceledException` and deletes the temporary root
- `sweepStaleRoots` deleting only old `datahubgit-checkout-*` folders (read-only files included) and counting them
- an empty include list, which keeps every LFS file a pointer and installs no LFS filter
- LFS hydration over a local `file://` remote (`workflows/**` hydrated, `runs/` left as pointers)
- a hostile `.lfsconfig` in the ARC, which cannot redirect the LFS download or exclude the requested paths

The live DataHUB checkout test is in `tests/WfHubClient.Tests/LiveTests.fs`, because it also packs
the working tree.
