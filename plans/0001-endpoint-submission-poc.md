# Endpoint Submission POC — Refocused Plan

## Goal

Prove that one service can ingest an ARC submission hook, capture everything that depends on the
short-lived DataHUB personal access token (PAT), persist one common submission record, and hand the user
to an endpoint-specific preview. ENA and WorkflowHub are developed side by side as explicit endpoint
implementations.

The POC is successful when adding another endpoint requires an explicit URL mapping, its own
`package_payload` handling, and its own preview page. It must not require a new submission table or a
generic plugin framework.

```text
ARC validation badge
  -> POST /api/v1/hooks/ena         -> /ena/submissions/{id}
  -> POST /api/v1/hooks/workflowhub -> /workflowhub/submissions/{id}
```

## Design stance

Endpoint behavior is deliberately explicit. ENA and WorkflowHub may need different payloads,
authenticated DataHUB reads, preview structures, credentials, and remote submission protocols. Do not
force those differences through a universal target-handler interface or dynamic registry during the
POC.

Share only behavior that is already identical and security-sensitive:

- authenticate the hook envelope and enforce `iat` freshness;
- validate and decompose `ro_crate_url` before any authenticated request;
- read submitter identity, commit, and the RO-Crate while the PAT is valid;
- persist the common submission provenance and immutable RO-Crate snapshot;
- enforce idempotency, mint status tokens, and maintain events;
- provide common page access checks, HTML escaping, and layout.

Each endpoint owns:

- its explicit hook route and handler;
- its `package_payload` model and validation;
- any additional PAT-dependent work during the hook request;
- its `follow_me` path and preview rendering;
- later, its credential form, remote client, and result interpretation.

## PAT lifecycle

The endpoint handler receives the complete initial `HookRequest`, including `gitlab_token`. It may pass
the PAT to common ingestion and use it for endpoint-specific DataHUB work, but only while that POST is
being handled.

```text
initial POST
  -> endpoint handler receives PAT
  -> endpoint payload validation
  -> common and endpoint-specific authenticated reads
  -> persist all preview inputs, including the RO-Crate snapshot
  -> return follow_me
  -> PAT falls out of scope

later preview GET
  -> status-link token authorizes access
  -> render only from persisted data
```

The PAT must never enter an entity, event, prepared artifact, log message, exception returned to the
caller, or background job. Endpoint credentials follow the same rule unless a future protocol provides
a safe short-lived replacement token.

## Common data model

ENA and WorkflowHub submissions are separate rows but the same `Submission` type. A submission records:

- DataHUB host, project id/name, resolved commit, and original RO-Crate URL;
- immutable RO-Crate JSON captured during the hook;
- raw endpoint `package_payload` JSON and the endpoint key;
- submitter identity/email, target environment, lifecycle status, and status-token hash;
- attempts, exact outbound payloads, receipts, external identifiers/accessions, and events.

Idempotency is scoped by DataHUB host, project, commit, endpoint, and target. Project ids alone are not
globally unique across DataHUB instances.

Avoid adding endpoint-specific columns to `Submission` merely for convenience. Keep endpoint options in
the raw payload until repeated real requirements justify promotion. The existing nullable `ReleaseDate`
is retained for the current POC and should be revisited once both previews settle.

## Current implementation

The baseline ENA intake implementation was checkpointed in commit `57f7937`.

The refocused implementation adds:

- explicit ENA and WorkflowHub hook handlers;
- a shared intake method that receives already-validated endpoint options;
- the original request, and therefore the PAT, at each endpoint handler boundary;
- persisted RO-Crate JSON for later preview rendering;
- DataHUB-host-scoped idempotency;
- endpoint-prefixed `follow_me` links and preview routes;
- a shared fixed-time token access check with endpoint-owned preview HTML.

The current previews prove routing, access, persistence, and customization. They do not yet construct or
submit complete ENA or WorkflowHub artifacts.

## Milestones

### P1 — Ingestion and endpoint separation

- Accept the common submission JSON at both explicit hook routes.
- Validate ENA and WorkflowHub payloads in their own handlers.
- Pass the initial request/PAT through the endpoint handler.
- Capture submitter, commit, and RO-Crate synchronously.
- Store both endpoints as the same `Submission` entity.
- Return a working endpoint-prefixed preview URL.

Acceptance: unit and route-level tests prove both endpoints, PAT use/non-persistence, idempotency, and
preview-token rejection. The PostgreSQL migration runs in the Compose stack.

### P2 — Useful endpoint previews

- Define the actual WorkflowHub `package_payload` contract.
- Render the endpoint-specific objects and actions that would be submitted.
- Make conversion/preparation happen during ingestion or from the persisted RO-Crate snapshot.
- Collect only endpoint-specific user input that cannot come from the ARC.
- Confirm that preview GETs perform no authenticated DataHUB calls.

Acceptance: the same ARC can be opened independently for ENA and WorkflowHub and each page shows a
meaningful, escaped preview from persisted data.

### P3 — Endpoint execution

Develop each path independently:

- ENA: Webin token exchange, submission, polling, and receipt/accession parsing.
- WorkflowHub: authentication, workflow registration/update, and remote identifier parsing.
- Both: discard credentials promptly and persist exact outbound payloads, receipts, attempts, external
  identifiers, and append-only events through the common tables.

Acceptance: sandbox/dev submissions can be driven from preview to a durable status page for each
endpoint.

### P4 — Integration and hardening

- Real AaaS badge-click tests against a test ARC.
- PostgreSQL and Docker Compose integration tests.
- Partial-failure and duplicate handling appropriate to each endpoint.
- Retry and concurrency behavior, safe error rendering, and useful operational logging.
- Backup/retention, privacy notice, notification path, and production enablement procedure.

## Open decisions

1. Add the validation commit SHA to the AaaS payload; resolving default-branch HEAD can capture a newer
   commit than the one that passed validation.
2. Specify WorkflowHub's endpoint payload and what its first useful preview must contain.
3. Decide whether each endpoint converts the persisted RO-Crate in-process or consumes a separately
   produced artifact.
4. Confirm whether AaaS can sign hook envelopes; signature verification would improve on the shared
   secret.
5. Define personal-data retention and the notification mechanism before external users rely on status
   links.

## Verification

```bash
dotnet test endpoint-submission.slnx
dotnet tool run dotnet-ef migrations has-pending-model-changes \
  --project src/EndpointSubmission.Service/EndpointSubmission.Service.csproj \
  --startup-project src/EndpointSubmission.Service/EndpointSubmission.Service.csproj
docker compose config --quiet
```

Before calling P1 complete, also start the Compose stack, apply migrations to PostgreSQL, and exercise
both hook and preview routes over HTTP.
