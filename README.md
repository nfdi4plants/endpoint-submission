# endpoint-submission

An ARC app that can trigger submission to selected endpoint repositories for ARCs passing defined validation packages.

Clicking a validation-package badge on an ARC's DataHUB homepage invokes a CQC hook endpoint here. The
service opens a submission, shows the user what will be sent, collects their credentials for the endpoint
repository, transacts with it, and tracks the outcome.

Because the badge is only clickable when the validation package passes, curation and submission-readiness
are upstream concerns. This service does not curate, does not collect missing metadata, and does not
convert — ARC → endpoint-format conversion belongs to a separate library.

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
tests/                               — xUnit
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
docker compose up -d --build
```

| | |
|---|---|
| Service | http://localhost:8081 |
| Health | http://localhost:8081/_health |
| OpenAPI | http://localhost:8081/swagger |
| Adminer | http://localhost:8080 |

Migrations are applied automatically in Development only; in production they are a deliberate deploy step.

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
| `HookSharedSecret` | — | Provisional caller authentication. Unset means the hook refuses to serve. |

Plus `PostgresConnectionString` at the root.

## Security notes

- **No credential of any kind is persisted.** Each endpoint handler receives the initial hook request,
  including its DataHUB PAT, so all common and endpoint-specific authenticated reads can finish during
  that request. The RO-Crate snapshot is retained; the PAT is discarded.
- **`ro_crate_url` is caller-supplied and gets fetched**, so it is allowlisted, HTTPS-only, and
  decomposed into generic-package components that the request URL is then rebuilt from — the address
  actually requested is one this service constructed, not a string the caller handed it. Without that it
  would be a server-side request forgery primitive holding a token.
- **Production requires two independent opt-ins** — deployment configuration *and* package payload.
  Submission mints permanent public identifiers that cannot be withdrawn.
- **The database is the only record that a submission happened.** Nothing is written back to the ARC, so
  backup and retention are correctness requirements, not ops hygiene.
