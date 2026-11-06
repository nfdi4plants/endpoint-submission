# WfHubClient

WfHubClient turns the workflows and runs of an ARC into Workflow RO-Crate zips ready for
deposition on [WorkflowHub](https://workflowhub.eu), and uploads them there on behalf of an
authenticated user. Packing works fully offline; checking ARCs out of a DataHUB is planned
next. The library started as the standalone `arc-to-workflowhub` repository and now lives here
beside the service that consumes it.

Every workflow in `workflows/` and every run in `runs/` is judged separately: first against the
same metadata requirements the `workflowhub` ARC validation package checks, then (only for
items that pass) by actually writing the RO-Crate and assembling the zip. Each item ends up
`Uploadable` (crate zip bytes included), `NotReady` (with every failed requirement's
explanation), or `PackFailed` (checks passed, but the crate could not be written, a referenced
file is missing, or the zip exceeds WorkflowHub's 100 MB upload limit).

## Is my ARC depositable?

Build once (`dotnet build -c Release`), save this as `check.fsx` in the repository root (the
`#r` path resolves relative to the script), and run `dotnet fsi check.fsx`:

```fsharp
#r "nuget: ARCtrl, 3.3.0"
#r "src/WfHubClient/bin/Release/net8.0/WfHubClient.dll"
open WfHubClient

let result = Pack.pack @"C:\path\to\my\arc"
result.ArcLoadError |> Option.iter (printfn "ARC could not be loaded: %s")

if result.License.IsNone then
    printfn "The ARC has no LICENSE file; WorkflowHub requires the crate to specify a license."

for item in result.Items do
    match item.Status with
    | Uploadable(_, size) -> printfn "%s: ready (%d bytes)" item.Identifier size
    | NotReady failures -> printfn "%s: not ready\n  - %s" item.Identifier (String.concat "\n  - " failures)
    | PackFailed error -> printfn "%s: packing failed: %s" item.Identifier error
```

## Licenses

WorkflowHub only accepts a license it can identify by its SPDX URL, so `pack` recognizes the
common license texts (MIT, Apache-2.0, BSD, the GNU family, MPL-2.0, Creative Commons,
Unlicense) from the ARC's `LICENSE` file and identifies the crate's license accordingly;
`PackResult.LicenseSpdxId` reports the identifier it settled on. An unrecognized license is
left unidentified rather than guessed. The upload then carries no license, and its owner can
set one on WorkflowHub afterwards.

## Uploading

`Client` talks to a WorkflowHub instance with a short-lived bearer token the caller supplies:
`getCurrentPerson` / `listTeams` for the team picker, `uploadCrate` (multipart POST, then a
policy PATCH that applies the chosen visibility; note the entry is briefly world-readable
between the two calls), `setVisibility` as the recovery call when that PATCH failed, and
`createVersion` for adding a version to an existing entry. The base URL is always an explicit
parameter. Pass `https://dev.workflowhub.eu` to rehearse against the sandbox; nothing defaults
to production. The client never deletes or overwrites, and tokens are never stored or logged.

## Layout

A single F# project, layered by compile order. The first six files are pure and depend on
ARCtrl only:

| Files | Contents |
|---|---|
| `Domain.fs`, `Api.fs`, `Protocol.fs`, `Licenses.fs`, `Requirements.fs`, `Readiness.fs` | readiness requirements, JSONAPI parsing and shaping, WorkflowHub API and OAuth constants; no I/O |
| `Pack.fs`, `Checkout.fs`, `Client.fs` | the `pack` pipeline (crate write plus zip assembly) and the HTTP client; checkout to follow |

Tests live in `tests/WfHubClient.Tests` and run offline with the rest of the solution. The
live WorkflowHub round trip is skipped unless you set `WFHUB_BASE_URL`, `WFHUB_TOKEN` and
`WFHUB_TEAM_ID` by hand; see the tests README.
