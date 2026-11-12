# Test notes

## Live WorkflowHub tests

The live tests in `LiveTests.fs` never run as part of a normal `dotnet test`; they are a
manual, opt-in step. They stay skipped unless all three environment variables are set:

```powershell
$env:WFHUB_BASE_URL = "https://dev.workflowhub.eu"   # a sandbox instance, never production
$env:WFHUB_TOKEN    = "<oauth access token>"
$env:WFHUB_TEAM_ID  = "<numeric project id>"
dotnet test tests/WfHubClient.Tests
```

Unset the variables again afterwards so later test runs stay offline.

The live suite uploads a minimal crate as `Private`, checks the entry's policy really is
`no_access`, adds a second version of that entry with `createVersion` and checks the entry keeps
its id, reaches version 2 and stays private, uploads another crate whose ARC carries a
recognizable license and checks WorkflowHub reports that license, and deletes both entries again
directly (the library itself has no delete). Besides exercising the client end to end it verifies the response-shape
assumptions the offline stub tests encode (`/people/current` relationships, `/projects/{id}`
titles, upload response fields).

`WFHUB_TOKEN` is an OAuth access token from that instance with the scopes `read write`. The
client sends it as `Authorization: Bearer <token>`. Register an OAuth application on your
WorkflowHub profile under Actions, API Applications, run the authorization code flow once by hand
and copy the access token from the token answer. WorkflowHub access tokens expire after about
two hours, so fetch a fresh one for each manual run.

## Checkout tests

The tests for the checkout itself are in `tests/DataHubGit.Tests`, see its README. The Pack guard
for workflow files that are LFS pointers is in `PackTests.fs`.

## Live DataHUB checkout test

The checkout and pack test in `LiveTests.fs` uses DataHubGit for the checkout. It stays skipped
unless these variables are set:

```powershell
$env:DATAHUB_HOST       = "gitdev.nfdi4plants.org"
$env:DATAHUB_PROJECT_ID = "<numeric project id>"
$env:DATAHUB_PAT        = "<personal access token>"
$env:DATAHUB_COMMIT     = "<optional 40 character commit id>"
dotnet test tests/WfHubClient.Tests
```

It checks out the project with `Pack.WorkflowHubLfsInclude` and a 5 minute timeout, asserts
that the commit id is hex (and equals `DATAHUB_COMMIT` when set), scans every file under the
temporary root for the token, runs `Pack.pack` on the working tree and prints each item's
identifier and status. It then disposes the checkout and asserts the root is gone. The test
only reads from the project. Unset the variables afterwards.

## Parity with the `workflowhub` validation package

The readiness requirements in `src/WfHubClient` are a port of the `workflowhub` ARC
validation package (`arc-validation-package-registry/StagingArea/workflowhub/workflowhub@0.0.1.fsx`),
and the fixture verdicts asserted here must match what that package reports for the same ARC
directories. The check is manual. Run it after changing `Requirements.fs`, `Readiness.fs`,
`FixtureArc.fs`, or the pinned ARCtrl version:

1. `dotnet build`, then write the fixture ARCs to disk with `dotnet fsi` (adjust the output
   root; keep it outside the repo):

   ```fsharp
   #r "nuget: ARCtrl, 3.3.1"
   #r @"src/WfHubClient/bin/Debug/net8.0/WfHubClient.dll"
   #r @"tests/WfHubClient.Tests/bin/Debug/net8.0/WfHubClient.Tests.dll"
   open WfHubClient.Tests.FixtureArc
   let root = @"C:\temp\parity"
   System.AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", root) // write target under fsi
   for name, build in
       [ "validArc", Fixtures.validArc
         "noTitleWorkflow", Fixtures.noTitleWorkflow
         // ... one entry per function in the Fixtures module ...
         "mixedArc", Fixtures.mixedArc ] do
       let written = write (build ())
       System.IO.Directory.Move(written.Directory, System.IO.Path.Combine(root, "arcs", name))
   ```

2. Run the package against every fixture directory (it validates the current working
   directory):

   ```powershell
   $fsx = "<path-to>\arc-validation-package-registry\StagingArea\workflowhub\workflowhub@0.0.1.fsx"
   Get-ChildItem "C:\temp\parity\arcs" -Directory | ForEach-Object {
       Push-Location $_.FullName
       dotnet fsi $fsx *> "..\..\logs\$($_.Name).log"
       Pop-Location
   }
   ```

3. Compare each log against the expectations encoded in `PackTests.statusCases`: an item this
   library reports `Uploadable` must pass every one of its test cases in the package run; an
   item reported `NotReady` must fail at least one (the same requirement). The package
   additionally runs the crate-write attempt as a requirement, so a `PackFailed` here also
   shows up as a package failure, just under that item's crate-write case. The license is the
   exception to the per-item rule. A missing or empty LICENSE makes every item `NotReady` with
   the `Readiness.licenseFailures` reason, while the package fails its critical "ARC" list and
   still passes each item's own cases. Its badge count of ready items ignores the license, so
   it can report items as ready that this library reports `NotReady`.
