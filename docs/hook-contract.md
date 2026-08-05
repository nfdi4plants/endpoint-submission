# ARC App Hook Contract

The [ARC specification](https://github.com/nfdi4plants/ARC-specification) defines a *(CQC) Hook Endpoint*
as *"an URL to trigger subsequent events based on the result of executing the validation package in a CQC
context"*, declared as optional validation-package metadata. It does not define what is passed to that
URL. This document specifies what **this** service accepts, with the intent of proposing it back to the
specification — any future ARC App needs the same shape.

## Invocation

```http
POST /api/v1/hooks/ena
Content-Type: application/json
X-Hook-Secret: <shared secret>
```

```json
{
  "iat": 1753866581,
  "package_endpoint": "https://<service_endpoint>",
  "package_payload": { "target": "sandbox", "contract_version": 1 },
  "project_id": 2,
  "project_name": "group/my-arc",
  "gitlab_token": "<short-lived token>",
  "ro_crate_url": "https://gitdev.nfdi4plants.org/api/v4/projects/2/packages/generic/isa_arc_json/0.0.1/arc-ro-crate-metadata.json"
}
```

| Field | Required | Notes |
|---|---|---|
| `iat` | yes | Issued-at, unix seconds. Rejected if older than `HookMaxAgeSeconds` (default 300) or more than 60s in the future. |
| `package_endpoint` | no | Recorded only; not used for dispatch. |
| `package_payload` | no | Endpoint-specific options, defined by the validation package author. See below. |
| `project_id` | yes | GitLab numeric project id. |
| `project_name` | yes | GitLab `path_with_namespace`. |
| `gitlab_token` | yes | Short-lived. Used only during this request; never stored. |
| `ro_crate_url` | yes | Must be `https`, on the configured DataHUB host allowlist, and a generic-package file URL (below). |

### Response

```json
{ "follow_me": "https://submit.example.org/submissions/<id>?t=<token>" }
```

| Status | Meaning |
|---|---|
| 200 | Submission opened (or re-opened). Send the user to `follow_me`. |
| 400 | Malformed payload, disallowed host, unknown target, unsupported contract version. |
| 401 | Missing/incorrect `X-Hook-Secret`, stale `iat`, or a `gitlab_token` the DataHUB rejected. |
| 502 | The DataHUB could not be read (commit unresolvable, RO-Crate unavailable). |
| 503 | This deployment has no hook secret configured and refuses to serve the endpoint. |

## `package_payload` — ENA

```json
{
  "contract_version": 1,
  "target": "sandbox",
  "checklist": "ERC000011",
  "release_date": "2027-01-01"
}
```

| Field | Default | Notes |
|---|---|---|
| `contract_version` | `1` | Payload schemas will change while deployed packages pin old versions. Anything other than `1` is rejected. |
| `target` | `"sandbox"` | `"sandbox"` or `"production"`. Unknown values are rejected rather than defaulted. |
| `checklist` | — | ENA sample checklist. |
| `release_date` | — | Default hold date offered on the preview page; the user may change it. |

## Behaviour worth knowing

**The token is used immediately and then discarded.** All DataHUB reads — user identity, commit
resolution, RO-Crate retrieval — happen inside the hook request. By the time a user opens `follow_me` the
token may be dead, so nothing is deferred to page-load time. No credential of any kind is persisted.

**`ro_crate_url` is fetched, so it is allowlisted *and* decomposed.** A caller-supplied URL retrieved while
holding a token is a server-side request forgery primitive. Only configured DataHUB hosts are permitted,
`https` only, and an empty allowlist fetches nothing. Beyond that, the URL must match the generic-package
shape:

```
https://<allowed host>/api/v4/projects/{projectId}/packages/generic/{packageName}/{version}/{fileName}
```

The service parses it into those four components and lets the DataHubClient library rebuild the request
URL from them, so the address actually requested is one this service constructed rather than a string the
caller supplied. `{projectId}` must equal the body's `project_id` — the two halves of the payload
disagreeing about which ARC is being submitted is never legitimate. Anything that does not decompose is
rejected, however trusted the host.

**Re-clicking is safe and is the recovery path.** A second click for the same project, commit, endpoint
and target returns the *same* submission with a freshly minted status link. The previous link stops
working. This is deliberate: a user who loses their status link gets it back by clicking the badge again.

**Production requires two independent opt-ins.** The payload must ask for it *and* the deployment must be
configured to allow it. Submission mints permanent public identifiers, so a single misconfiguration must
not be able to cause one.

**Caller authentication is a shared secret, deliberately.** `X-Hook-Secret` is required and the service
refuses to serve the hook when none is configured rather than falling open. It is defence in depth: the
load-bearing authenticator is `gitlab_token`, since no submission can be created without one the DataHUB
accepts. One consequence to keep in mind — the `iat` freshness check only deters replay to the extent the
envelope is authenticated, so if AaaS signs its payloads, verifying that signature would be a strict
improvement over the shared secret rather than a parallel mechanism.

## Open

- **No commit hash is supplied.** `ro_crate_url` carries a package version, not a commit, so the service
  resolves the project's default-branch HEAD instead. That is *"HEAD when the badge was clicked"*, which
  differs from *"the commit the validation package ran against"* whenever the ARC moves in between.
  Adding `commit_sha` to the payload would close the gap.
