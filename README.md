# endpoint-submission

An ARC app that can trigger submission to selected endpoint repositories for ARCs passing defined validation packages.

Clicking a validation-package badge on an ARC's DataHUB homepage invokes a CQC hook endpoint here. The
service opens a submission, shows the user what will be sent, collects their credentials for the endpoint
repository, transacts with it, and tracks the outcome.

Because the badge is only clickable when the validation package passes, curation and submission-readiness
are upstream concerns. This service does not curate, does not collect missing metadata, and does not
convert — ARC → endpoint-format conversion belongs to separate libraries. For WorkflowHub that library
is [WfHubClient](src/WfHubClient/README.md), maintained in this repo as its own project.

## Status

The ingestion foundation now has explicit ENA and WorkflowHub hook handlers backed by one submission
model. Both capture the RO-Crate while the short-lived DataHUB PAT is valid and return an
endpoint-specific preview URL. The preview pages currently show captured provenance only; nothing is
submitted to either repository yet. See [plans/0001-endpoint-submission-poc.md](plans/0001-endpoint-submission-poc.md)
for the refocused POC and acceptance criteria.

## Layout

Mirrors [AVPR](https://github.com/nfdi4plants/arc-validate-package-registry): a C# ASP.NET Core service
with F# libraries beside it.

```
src/EndpointSubmission.Service   C#  — shared ingestion, endpoint handlers, pages, EF Core/Npgsql
  Endpoints/ENA                      — ENA payload handling and preview
  Endpoints/WorkflowHub              — WorkflowHub payload handling and preview
src/ENAClient                    F#  — future Webin v2 execution client
src/WfHubClient                  F#  — ARC to WorkflowHub packing and upload client
tests/                               — xUnit and Expecto
docs/hook-contract.md                — the ARC App invocation contract
plans/                               — design record
```

DataHUB reads go through [DataHubClient](https://github.com/nfdi4plants/DataHubClient), DataPLANT's
polyglot ARC DataHub library, behind the `IArcMetadataReader` port in
[ArcMetadataReader.cs](src/EndpointSubmission.Service/Services/ArcMetadataReader.cs). The port exists to
keep F# `Async`/`Option` types out of the service and to keep intake testable without a transport;
`client.Users.GetCurrentAsync()` there is the call this project contributed upstream.

## Running

```bash
cp .env.example .env
docker compose up -d --build
```

Edit `.env` for the local deployment. It is ignored by Git; `.env.example` is the committed list of
supported service variables and safe development defaults.

| | |
|---|---|
| Service | http://localhost:8081 |
| Health | http://localhost:8081/_health |
| OpenAPI | http://localhost:8081/swagger |
| Adminer | http://localhost:8080 |

The service applies migrations at startup only in the Development environment. Other environments run the
image with `--migrate` as a separate deploy step. With Compose that is
`docker compose run --rm endpointsubmissionservice --migrate`.

```bash
dotnet test          # unit tests, no database or network required
```

## Configuration

Under the `EndpointSubmission` section (or `EndpointSubmission__*` environment variables):

| Setting | Default | Purpose |
|---|---|---|
| `AllowedDataHubHosts` | `[]` | Hosts whose URLs may be fetched. Empty fetches nothing. |
| `AllowProductionTarget` | `false` | Master switch for production submission. |
| `PublicBaseUrl` | — | Origin used to build `follow_me` URLs. |
| `HookMaxAgeSeconds` | `300` | How stale an `iat` may be. |
| `HookSharedSecret` | — | Provisional caller authentication outside Development. Unset means the hook refuses to serve. |

Plus `PostgresConnectionString` at the root, and `WorkflowHub__PreparationTimeoutSeconds` (default `180`,
1 to 3600): the seconds the ARC checkout may take before the WorkflowHub hook records a failure and
answers anyway. Packing starts after the checkout and runs to the end (usually seconds), so the hook can
answer somewhat later than this limit.

### WorkflowHub login

The WorkflowHub upload logs the user in with OAuth: authorization code grant with PKCE S256, as a
public client. The service holds no client secret. The user ticks items on the preview page, clicks
"Continue to WorkflowHub", logs in and confirms there, and lands on `/workflowhub/oauth/callback`. That
request exchanges the code for an access token, lists the user's teams, uploads and revokes the token.

Register one OAuth application per WorkflowHub instance under My Profile, Actions, API Applications.
Make it a non-confidential application with the scopes `read write` and the redirect URI
`{PublicBaseUrl}/workflowhub/oauth/callback`. WorkflowHub (Doorkeeper) rejects http redirect URIs
outside its development mode, so `PublicBaseUrl` needs https, also for a local deployment. Then set the
client id of the application:

| Setting | Default | Purpose |
|---|---|---|
| `WorkflowHub__Sandbox__ClientId` | empty | Client id on `dev.workflowhub.eu`. Empty turns the upload login off for sandbox submissions. |
| `WorkflowHub__Production__ClientId` | empty | Client id on `workflowhub.eu`. Production submissions also need `AllowProductionTarget`. |
| `WorkflowHub__ProductionDataHubHosts__0` | empty | A DataHUB host (add `__1`, `__2` for more) whose hooks go to production when they name no target. Hooks that name a target keep it. Production submissions also need `AllowProductionTarget`. |
| `WorkflowHub__OAuthFlowMinutes` | `10` | How long the user may take on WorkflowHub (1 to 60). |

The service keeps the pending login in the cookie `wfh_oauth`. The cookie is encrypted with a key ring
that lives in memory, so a restart ends the logins that are in progress. The user then opens the
submission from the badge link again. A browser holds one pending WorkflowHub login at a time, so
starting a second upload in another tab replaces the first.

In the `Development` environment, hook authentication is bypassed by a development-only
`IHookAuthenticator` registration. Other environments always use the shared-secret authenticator and
fail closed when `HookSharedSecret` is absent.

## Security notes

- **No credential of any kind is persisted.** Each endpoint handler receives the initial hook request,
  including its DataHUB PAT, so all common and endpoint-specific authenticated reads can finish during
  that request. The RO-Crate snapshot is retained; the PAT is discarded.
- The WorkflowHub access token lives in one local variable of the OAuth callback request. That request
  revokes it before it answers. The encrypted login cookie holds the submission id, the ticked items with
  their visibility, the chosen team, the OAuth state, the PKCE verifier, the status token of the preview
  page and the time the login expires. It holds no WorkflowHub token and no PAT.
- **`ro_crate_url` is caller-supplied and gets fetched**, so it is allowlisted, HTTPS-only, and
  decomposed into generic-package components that the request URL is then rebuilt from — the address
  actually requested is one this service constructed, not a string the caller handed it. Without that it
  would be a server-side request forgery primitive holding a token.
- **Production requires two independent opt-ins** — deployment configuration *and* package payload.
  For WorkflowHub, a DataHUB host listed in `WorkflowHub__ProductionDataHubHosts` counts as the request
  for production when the hook names no target, and `AllowProductionTarget` is still required.
  Submission mints permanent public identifiers that cannot be withdrawn.
- **The database is the only record that a submission happened.** Nothing is written back to the ARC, so
  backup and retention are correctness requirements, not ops hygiene.
