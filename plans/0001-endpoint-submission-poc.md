# Endpoint Submission — Requirements, Decisions & Implementation Plan

Record of the requirements conversation, the decisions it settled, and the resulting build plan.
Destination: `plans/` in this repository.

## What this is

An **ARC App**: a CQC hook endpoint that submits ARC contents to an endpoint repository, triggered by
clicking a validation-package badge on an ARC's DataHUB homepage.

```text
ARC commit → CI runs validation package → badge on ARC homepage
           → clickable only if all critical cases passed
           → click → AaaS POSTs to this service → { "follow_me": url }
           → preview → user's endpoint credentials → repository
```

## Trigger contract

```json
{
  "iat": 1753866581,
  "package_endpoint": "https://<service_endpoint>",
  "package_payload": { "...": "package-author-defined" },
  "project_id": 2,
  "project_name": "<project_with_namespace>",
  "gitlab_token": "<short-lived token>",
  "ro_crate_url": "https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/isa_arc_json/0.0.1/arc-ro-crate-metadata.json"
}
```

Response: `{ "follow_me": "<url>" }`.

Three consequences:

- **The token dies young.** An unknown amount of time passes before the user opens `follow_me`. All
  token-dependent work — fetch RO-Crate, resolve commit, read submitter identity — must happen *during*
  the hook request, then the token is discarded. Lazy fetching on page load works in testing and fails
  the first time someone opens the link ten minutes later.
- **`package_payload` is the configuration channel.** Package-author-defined and opaque to AaaS, so
  endpoint options live here. Needs a documented schema per endpoint, validated on receipt.
- **No commit hash is provided.** `ro_crate_url` carries a package version, not a commit. Resolvable via
  the GitLab API, but that risks recording a different commit than the badge was generated from. Better
  to request `commit_sha` be added to the payload.

## Decisions

| Topic | Decision |
|-------|----------|
| Scope of this service | **Facilitation only** |
| Conversion | Out of scope — separate library/workflow |
| Curation / backfill | Out of scope — upstream validation is the gate |
| Endpoint | ENA (but see open question 1) |
| POC depth | Metadata registration; file transfer is phase 2 |
| Endpoint credentials | User pass-through, never persisted |
| DataHUB access | Short-lived `gitlab_token` from AaaS |
| Authorization | Clicker is sufficient |
| Accessions | Stored and displayed; no ARC write-back |
| Status access | Signed unguessable link + submitter email gate |
| Stack | ASP.NET Core 8 service in **C#**, consuming F# libraries; PostgreSQL; docker-compose (mirroring AVPR) |
| Frontend | Server-rendered interpolated strings, AVPR-style |

### On "clicker is sufficient"

Defensible because the endpoint credential requirement is a second de facto gate — no submission without
the user's own repository account, limiting exposure to existing credential holders. Two consequences:
the audit trail carries all the weight, since nothing else attests to authorization (record the clicker's
GitLab identity on every submission); and the preview page should state plainly that this makes data
permanently public, as information rather than as a gate.

### On the frontend

Server-rendered is right for three pages (preview, credentials, status). Caveats, not objections: the
status page needs auto-refresh since processing is async; the credential form must be POST-only and
scrubbed from logs; an ENA preview is several XML documents plus a sample table, so section it.

## Findings

**ENA offers no OAuth.** Every ENA API is HTTP Basic with the Webin username and password — no
authorization-code flow, no scoped grant, no EBI/Elixir AAI. No design avoids this service handling the
user's actual password at least once. A token exchange does exist
(`POST /ena/submit/webin/auth/token` → JWT, ~3h): exchange on arrival, discard the password, run
downstream work on the bearer token. The 3h TTL covers a file transfer but not multi-day report polling —
prefer user-initiated status refresh over storing credentials for background polling.

**ENA API surface is Webin v2**, not the legacy drop-box: `/ena/submit/webin-v2/submit`, plus
`/submit/queue` and `/submit/poll/{id}`. ENA models long-running submission itself, so phase 2 mirrors
ENA's `submissionId` rather than inventing progress tracking. Test server is at `wwwdev.ebi.ac.uk`,
rebuilt from production daily at 03:00, submissions discarded within 24h — so no test may assume a dev
accession persists.

**AVPR package inventory** — `pride` (1.0.4, publishability gate), `insdc_curation` (0.0.2, curation
metadata), `invenio` (3.2.0), `edal`, `odrl`, `ontology-terms`, others. No `ena` package published.

**The ARC spec defines the hook concept but not its parameters** — worth proposing the contract above
back to the spec, since any future ARC App needs the same shape.

## Settled during M1

- **Caller authentication.** Shared secret in `X-Hook-Secret`, required; the service refuses to serve the
  hook when none is configured rather than falling open. Accepted as the mechanism, not as a placeholder.
  It is defence in depth — the load-bearing authenticator is `gitlab_token`, since no submission can be
  created without one the DataHUB accepts. Corollary: the `iat` freshness check only deters replay to the
  extent the envelope is authenticated, so if AaaS turns out to sign its payloads, verifying that
  signature would strictly improve on the shared secret.
- **SSRF via `ro_crate_url`.** Closed by `DataHubUrlGuard`: HTTPS only, explicit host allowlist, empty
  allowlist fetches nothing, and the check runs before any outbound request.
- **Link loss.** Closed by re-click: clicking the badge again returns the same submission with a freshly
  minted status token. The old link stops working.
- **Contract versioning.** `contract_version` is present in `package_payload` from the first release;
  anything other than `1` is rejected rather than defaulted.

## Still open

1. **ENA vs PRIDE as POC target.** PRIDE's gating package is mature (1.0.4); ENA's nearest is
   `insdc_curation` at 0.0.2. If the gate is a co-requisite, PRIDE is further along — but PRIDE brings
   Aspera and a weeks-long curation queue.
2. **Where does conversion run?** In-process library call against `ro_crate_url`, or a pre-produced
   artifact fetched alongside it? Determines whether this service takes a dependency on the conversion
   library or stays a pure HTTP consumer.
3. **`commit_sha` is not in the AaaS payload.** The service resolves default-branch HEAD instead, which
   is "HEAD when the badge was clicked", not "the commit the validation package ran against". Adding
   `commit_sha` to the payload would close the gap.
4. **Does one deployment serve multiple DataHUB instances?** Shapes the host allowlist and whether
   submissions need an instance dimension beyond the recorded `DataHubHost`.
5. **Partial failure.** Study accepted, samples rejected — doesn't fit a single status enum. Lands in M4.
6. **The database is the only record a submission happened.** No ARC write-back means no audit trail
   elsewhere; backup and retention become correctness requirements.
7. **Personal data.** Submitter email and GitLab identity stored — retention policy, deletion, privacy
   notice.
8. **Notification path.** Signed-link model implies email — is there SMTP available, or is that new
   infrastructure?

## Implementation plan

**Mirror AVPR's layout as closely as possible** — same split (C# ASP.NET Core service, F# libraries
beside it), same internal service structure, same compose topology. Deviate only where this service
genuinely differs, and say so when doing it.

```text
/src
  EndpointSubmission.Service/       C# — mirrors AVPR's PackageRegistryService structure:
    .config/  API/  Authentication/  Data/  Migrations/  Models/
    OpenAPI/  Pages/  Properties/  wwwroot/
    Program.cs  Dockerfile  appsettings.json  appsettings.Development.json
    EndpointSubmission.Service.csproj
  ENAClient/                        F# — Webin v2 client, auth token exchange, receipt parsing
/tests
  ENAClient.Tests/                  F# — receipt parsing, token exchange, over recorded responses
  EndpointSubmission.Tests/         C# — hook contract, payload validation, status-page gating
/docs/hook-contract.md
endpoint-submission.sln
docker-compose.yml
```

`docker-compose.yml` follows AVPR's shape: service + `postgres` + `adminer` on one bridge network, the
service depending on the db, db reachable by service name, adminer on 8080. Copy AVPR's Dockerfile and
`Program.cs` bootstrap conventions rather than inventing new ones.

AVPR's `Pages/` contains **no Razor at all** — `PageEndpoints.cs` maps minimal-API routes,
`Handlers/*.cs` return `TypedResults.Text(..., "text/html")`, and `Components/*.cs` are static classes
whose `Render()` methods build interpolated HTML around a `Layout.Render(...)` shell. PicoCSS is vendored
into `wwwroot/css/`, not pulled from a CDN. Mirror that exactly.

One consequence the pattern does not hand you: interpolating into raw HTML makes every value that did not
come from the page source an injection vector, and most of what these pages show does not — project names
and submitter addresses come from the DataHUB, and `package_payload` fields come straight from the hook
caller. Everything interpolated goes through an escaping helper; only literal markup is interpolated raw.

**M1 — Skeleton and hook.** Solution, compose stack, EF Core migrations for the data model below.
`POST /ena` validates `package_payload` against a documented schema, checks `ro_crate_url` against a
DataHUB host allowlist, then does all token-dependent work inline: fetch RO-Crate, resolve `commit_sha`,
read submitter identity/email from the GitLab API. Persists, **discards the token**, returns `follow_me`.
Repeated clicks for the same project+commit return the existing submission.

**M2 — Artifact seam and preview.** `ISubmissionArtifactSource` with a fixture-backed stub, so the whole
path is buildable before the conversion library exists. Preview page renders the submission objects
sectioned, plus the two unavoidable inputs: release/hold date and confirmation.

**M3 — ENA client.** F# library: token exchange, `webin-v2/submit`, receipt parsing. Tested against
recorded success and failure responses — no account needed.

**M4 — Submit and status.** Credential POST → exchange for JWT → discard password → submit → persist
payload, receipt, accessions, events. Status page behind signed link + email gate, auto-refreshing.

**M5 — Hardening.** Partial-failure rendering, duplicate-alias surfacing, negative paths, and
`docs/hook-contract.md` written up for proposal back to the ARC spec.

### Data model

`submissions` (project, resolved commit, `ro_crate_url`, raw `package_payload`, target, status,
submitter identity/email, `status_token_hash`, release date) · `submission_attempts` (a resubmission is
an attempt, not a new submission) · `submission_payloads` (exact XML sent) · `submission_receipts` ·
`accessions` · `submission_events` (append-only; renders the status page) · `notifications`.

Neither `gitlab_token` nor endpoint credentials appear in this schema, by design.

### Invariants

Sandbox target by default — production requires explicit configuration *and* explicit payload opt-in.
Aliases derived deterministically from project path and object identity, **excluding** the commit, so a
resubmission refers to the same object rather than duplicating it; phase 1 is ADD-only and surfaces
duplicate-alias rejections plainly.

### Verification

Unit: receipt parsing, payload validation, gate logic. Contract: documented hook body creates a
submission and returns `follow_me`; malformed, replayed, and duplicate posts behave. Live: submit against
the ENA **dev** server with a test Webin account. Integration: `docker-compose up`, real badge click on a
DataHUB test ARC, through preview → submit → `follow_me` → email gate → status. Negative: expired token,
artifact-source error, production requested without opt-in must refuse.

### Starts blocked on

Open questions 1–3 (endpoint target, conversion location, caller authentication). M1–M2 can proceed
regardless; M3 needs a Webin test account; M4's live path needs both.
