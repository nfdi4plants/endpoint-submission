# Repository guidance

## Architecture

- Keep endpoint flows explicit. ENA lives under `Endpoints/ENA`; WorkflowHub lives under
  `Endpoints/WorkflowHub`. Add a new endpoint with its own hook mapping, payload handling, and preview.
- Do not introduce a dynamic target registry, plugin framework, or universal endpoint interface unless
  several implemented endpoints demonstrate a concrete shared abstraction.
- Share only the common ingestion, persistence, status-token access, HTML shell, and other behavior that
  is genuinely identical.
- Store all endpoint submissions as the same `Submission` entity, distinguished by `Endpoint`. Do not
  add a table or entity subtype per endpoint.

## Credential boundary

- Every endpoint hook handler must receive the original `HookRequest`, including `gitlab_token`.
- Complete all common and endpoint-specific PAT-dependent work during that initial POST.
- Persist the immutable inputs needed by preview pages; preview requests must not require the PAT.
- Never persist or log a PAT, endpoint password, bearer token, or other credential. Do not include one in
  entities, events, exceptions returned to callers, prepared artifacts, or background jobs.
- Pass credentials as method arguments scoped to the request. Never retain them in service fields.
- Tests that add persisted string fields must continue to prove that the submitted PAT occurs nowhere in
  the stored object graph.

## Routes and pages

- Hook routes are explicit: `/api/v1/hooks/ena`, `/api/v1/hooks/workflowhub`, and equivalents for future
  endpoints.
- Preview routes are endpoint-prefixed: `/{endpoint}/submissions/{id}`.
- Endpoint code owns preview content. Common page code owns token access checks, escaping, and layout.
- Escape every value derived from a hook, DataHUB, database, or endpoint response before placing it in
  interpolated HTML.

## Data and migrations

- A submission row represents one ARC commit submitted to one endpoint and target environment.
- Scope idempotency by DataHUB host, project id, commit, endpoint, and target.
- Keep raw endpoint `package_payload` JSON for auditability. Prefer it over endpoint-specific columns
  until a common query or invariant requires a promoted field.
- Preserve exact outbound payloads and raw receipts when execution is implemented.
- Add an EF Core migration for every model change and verify that the model snapshot is current.

## Verification

- Run `dotnet test endpoint-submission.slnx` after code changes.
- Run `dotnet tool run dotnet-ef migrations has-pending-model-changes --project
  src/EndpointSubmission.Service/EndpointSubmission.Service.csproj --startup-project
  src/EndpointSubmission.Service/EndpointSubmission.Service.csproj` after model changes.
- Run `docker compose config --quiet` after Compose changes.
- Add endpoint-specific tests for payload validation and preview rendering, plus shared tests for PAT
  non-persistence, access control, idempotency, and malformed hook envelopes.
- Do not treat placeholder tests or an in-memory EF test as evidence that PostgreSQL or a remote endpoint
  integration works.
