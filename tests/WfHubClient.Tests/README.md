# Test notes

## Live WorkflowHub tests

The live tests in `LiveTests.fs` never run as part of a normal `dotnet test`; they are a
manual, opt-in step. They stay skipped unless all three environment variables are set:

```powershell
$env:WFHUB_BASE_URL = "https://dev.workflowhub.eu"   # a sandbox instance, never production
$env:WFHUB_TOKEN    = "<api token>"
$env:WFHUB_TEAM_ID  = "<numeric project id>"
dotnet test tests/WfHubClient.Tests
```

Unset the variables again afterwards so later test runs stay offline.

The live suite uploads a minimal crate as `Private`, checks the entry's policy really is
`no_access`, uploads a second crate whose ARC carries a recognizable license and checks
WorkflowHub reports that license, and deletes both entries again directly (the library itself
has no delete). Besides exercising the client end to end it verifies the response-shape
assumptions the offline stub tests encode (`/people/current` relationships, `/projects/{id}`
titles, upload response fields).

`WFHUB_TOKEN` is an OAuth Bearer token. On an instance whose login form is behind a bot
challenge, register a confidential OAuth application (`urn:ietf:wg:oauth:2.0:oob`, scopes
`read write`) from the authenticated pages and run the authorization code grant against
`/oauth/authorize` and `/oauth/token`; send `Accept: application/json` on the token request,
or the endpoint answers 500.

## Parity with the `workflowhub` validation package

The readiness requirements in `src/WfHubClient` are a port of the `workflowhub` ARC
validation package (`arc-validate-package-registry/StagingArea/workflowhub/workflowhub@0.0.1.fsx`),
and the fixture verdicts asserted here must match what that package reports for the same ARC
directories. The check is manual. Run it after changing `Requirements.fs`, `Readiness.fs`,
`FixtureArc.fs`, or the pinned ARCtrl version:

1. `dotnet build`, then write the fixture ARCs to disk with `dotnet fsi` (adjust the output
   root; keep it outside the repo):

   ```fsharp
   #r "nuget: ARCtrl, 3.2.0"
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
   $fsx = "<path-to>\arc-validate-package-registry\StagingArea\workflowhub\workflowhub@0.0.1.fsx"
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
   shows up as a package failure, just under that item's crate-write case. License verdicts
   appear in the package's critical "ARC" list, matching `PackResult.License` and
   `Readiness.licenseFailures`.
