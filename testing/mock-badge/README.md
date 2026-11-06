# Mock badge

The mock badge is a small web page that stands in for the DataHUB validation badge. It lets you test the WorkflowHub hook of the endpoint submission service without AaaS. It is a test instance, separate from the service. The service does not reference it, and `endpoint-submission.slnx` does not include it.

The page has one button. A click sends the hook request that AaaS would send to `POST /api/v1/hooks/workflowhub`. The body carries `iat`, `project_id`, `project_name`, `gitlab_token`, `ro_crate_url` and a `package_payload` of `{"contract_version": 1, "target": "sandbox"}`. The mock waits for the answer, which can take minutes because the service checks the ARC out and packs crates. On a 200 response it redirects the browser to the returned `follow_me` URL. Any other answer appears on an error page with the status code and the `error` or `detail` text of the response.

The target is always `sandbox`. The page shows a configuration error for any other `MOCK_TARGET`.

## Configuration

The mock reads environment variables. Docker Compose loads them from `mock.env`.

| Variable | Required | Meaning |
| --- | --- | --- |
| `MOCK_HOOK_BASE_URL` | yes | Base URL of the service, for example `http://endpointsubmissionservice:8080`. |
| `MOCK_PROJECT_ID` | yes | GitLab project id (integer). |
| `MOCK_PROJECT_NAME` | yes | GitLab path with namespace, sent as `project_name`. |
| `MOCK_RO_CRATE_URL` | yes | RO-Crate metadata URL, sent as `ro_crate_url`. The page takes the DataHUB host from it. |
| `MOCK_DATAHUB_PAT` | yes, to click | Your DataHUB token with the `read_api` and `read_repository` scopes. The service reads the project over the API and checks the ARC out with git, which needs `read_repository`. |
| `MOCK_TARGET` | no | Defaults to `sandbox`. No other value is accepted. |
| `MOCK_HOOK_SECRET` | no | Sent as `X-Hook-Secret`. The service needs it only outside Development. |

When a required variable is missing or invalid, the page lists the variable names and disables the button. It never prints values.

The mock exits at startup unless `MOCK_HOOK_BASE_URL` uses https or, for plain http, names a loopback address or a host without a dot such as `endpointsubmissionservice`, because every hook request carries the DataHUB token.

## Run it locally

Start the main stack as usual (the service needs its own `.env`, copied from `.env.example`). Then, from the repository root:

```
cp testing/mock-badge/mock.env.example testing/mock-badge/mock.env
```

Open `testing/mock-badge/mock.env` and set `MOCK_DATAHUB_PAT`. Then run:

```
docker compose -f docker-compose.yml -f testing/mock-badge/docker-compose.mock.yml up -d --build
```

Open http://localhost:8082 and click the button. The container publishes port 8082 on the loopback interface only. The service's `EndpointSubmission__PublicBaseUrl` is `http://localhost:8081` by default, so the redirect to `follow_me` opens in the same browser.

To stop the mock without touching the rest of the stack:

```
docker compose -f docker-compose.yml -f testing/mock-badge/docker-compose.mock.yml stop mock_badge
```

## Use it on the VM

On the VM, add the VM override file to the compose command:

```
docker compose -f docker-compose.yml -f testing/mock-badge/docker-compose.mock.yml -f testing/mock-badge/docker-compose.vm.yml up -d --build
```

The main file publishes Postgres on 5432 (password `postgres`) and Adminer on 8080 on every interface. It publishes the service on 8081 the same way. Docker-published ports bypass the host firewall, so anyone who can reach the VM could reach them. The override file rebinds each of these ports to the loopback interface. Every published port is then loopback-only, including the mock on 8082, and you reach the stack through an SSH tunnel from your own machine:

```
ssh -N -L 18081:localhost:8081 -L 18082:localhost:8082 debian@<vm>
```

Open http://localhost:18082 in your browser. Set the service's `EndpointSubmission__PublicBaseUrl` to `http://localhost:18081`. The `follow_me` URL then opens through the tunnel. `MOCK_HOOK_BASE_URL` stays `http://endpointsubmissionservice:8080`, because the mock reaches the service over the compose network.

The stack runs in Development on the VM. In that mode the service applies the database migrations at start and skips hook authentication. That is acceptable only because nothing listens on a public interface. `MOCK_HOOK_SECRET` is needed only when the service runs outside Development.

The override file uses the `!override` tag, which needs Docker Compose 2.24 or later.

## The token

The token comes from `mock.env` on the host. Git ignores that file (the root `.gitignore` lists `testing/mock-badge/mock.env`). The page shows only "token configured: yes" or "no". The token leaves the host only in the hook request to the service, over the compose network. The mock logs status codes only. Error text from the service appears on the error page with every occurrence of the token replaced by `[REDACTED]`.

Use a short-lived token and revoke it after testing.
