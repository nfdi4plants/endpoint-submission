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
| `MOCK_HOOK_SECRET` | no | Sent as `X-Hook-Secret`. The service checks it only outside Development, which this mock stack does not support (see below). Leave it empty. |

When a required variable is missing or invalid, the page lists the variable names and disables the button. It never prints values.

The mock exits at startup unless `MOCK_HOOK_BASE_URL` uses https or, for plain http, names a loopback address or a host without a dot such as `endpointsubmissionservice`, because every hook request carries the DataHUB token.

The mock stack runs the service in the `Development` environment, which `.env.example` sets with `ASPNETCORE_ENVIRONMENT=Development`. Outside Development the service redirects http to https. The hook call of the mock goes over the compose network to `http://endpointsubmissionservice:8080`, so it would follow that redirect to a certificate issued for `localhost` and fail. Keep Development in `.env` while you use the mock.

## Certificate

WorkflowHub rejects http redirect URIs, localhost included. The service therefore also listens on https (port 8443 in the container, published as 8081). The compose files mount `testing/mock-badge/certs/` read-only at `/https` and the service reads `localhost.crt` and `localhost.key` from there. Both files are PEM and the key is unencrypted. Git ignores the folder.

On your own machine, export the ASP.NET development certificate. From the repository root:

```
mkdir -p testing/mock-badge/certs
dotnet dev-certs https -ep testing/mock-badge/certs/localhost.crt --format PEM --no-password
```

The command writes `localhost.crt` and `localhost.key`. Your browser trusts the certificate only when the development certificate is trusted on the machine. `dotnet dev-certs https --check --trust` tells you, and `dotnet dev-certs https --trust` trusts it (Windows and macOS).

On Linux and macOS, the service container runs as uid 1654, so make both files readable by everyone:

```
chmod 644 testing/mock-badge/certs/localhost.crt testing/mock-badge/certs/localhost.key
```

On the Debian VM, create a self-signed certificate:

```
mkdir -p testing/mock-badge/certs
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" -keyout testing/mock-badge/certs/localhost.key -out testing/mock-badge/certs/localhost.crt
chmod 644 testing/mock-badge/certs/localhost.crt testing/mock-badge/certs/localhost.key
```

The service container runs as uid 1654, so both files must be readable by everyone. The browser shows a certificate warning once for `https://localhost:18081`. Accept it. The certificate is valid for 30 days.

## Register the OAuth application

The service logs in to WorkflowHub with OAuth. The application is a public client with PKCE, so the service needs no client secret. Register it once per WorkflowHub instance with your own account:

1. Sign in on https://dev.workflowhub.eu.
2. Open My Profile, then Actions, then API Applications, and create a new application.
3. Enter a name of your choice, for example `endpoint-submission test`.
4. Leave Confidential unticked.
5. Select the scopes `read` and `write`.
6. Enter the redirect URIs, one per line. Use `https://localhost:8081/workflowhub/oauth/callback` for the local stack and `https://localhost:18081/workflowhub/oauth/callback` for the VM through the SSH tunnel.
7. Save the application and copy its UID.

The service builds the callback URL from `EndpointSubmission__PublicBaseUrl`, so the registered URI must match it exactly, port included. Put the UID into `.env`:

```
WorkflowHub__Sandbox__ClientId=<application UID>
```

## Run it locally

The service needs its own `.env`, copied from `.env.example`. The example file has `EndpointSubmission__PublicBaseUrl=http://localhost:8081`, so set `EndpointSubmission__PublicBaseUrl=https://localhost:8081` in your `.env`. Then, from the repository root:

```
cp testing/mock-badge/mock.env.example testing/mock-badge/mock.env
```

Open `testing/mock-badge/mock.env` and set `MOCK_DATAHUB_PAT`. Create the certificate and register the OAuth application as described above. Then run:

```
docker compose -f docker-compose.yml -f testing/mock-badge/docker-compose.mock.yml -f testing/mock-badge/docker-compose.vm.yml up -d --build
```

The local stack uses the VM file too. It is the only file that maps port 8081 to the https port of the service, and on a local machine it also keeps every published port on the loopback interface.

Open http://localhost:8082 and click the button. The mock calls the hook over plain http on the compose network and redirects the browser to the returned `follow_me` URL, which starts with `https://localhost:8081`. The container publishes port 8082 on the loopback interface only.

To stop the mock without touching the rest of the stack:

```
docker compose -f docker-compose.yml -f testing/mock-badge/docker-compose.mock.yml stop mock_badge
```

## Use it on the VM

On the VM, create the self-signed certificate and run the same three-file command as locally:

```
docker compose -f docker-compose.yml -f testing/mock-badge/docker-compose.mock.yml -f testing/mock-badge/docker-compose.vm.yml up -d --build
```

The main file publishes Postgres on 5432 (password `postgres`) and Adminer on 8080 on every interface. It publishes the service on 8081 the same way. Docker-published ports bypass the host firewall, so anyone who can reach the VM could reach them. The override file rebinds each of these ports to the loopback interface. Every published port is then loopback-only, including the mock on 8082, and you reach the stack through an SSH tunnel from your own machine:

```
ssh -N -L 18081:localhost:8081 -L 18082:localhost:8082 <user>@<vm>
```

Open http://localhost:18082 in your browser and click the button. Set the service's `EndpointSubmission__PublicBaseUrl` to `https://localhost:18081` in the VM's `.env`. The `follow_me` URL then opens on https://localhost:18081 through the tunnel, and so does the OAuth callback. `MOCK_HOOK_BASE_URL` stays `http://endpointsubmissionservice:8080`, because the mock reaches the service over the compose network, where the service keeps its http port 8080.

The stack runs in Development on the VM. In that mode the service applies the database migrations at start and skips hook authentication. That is acceptable only because nothing listens on a public interface. The service ignores `X-Hook-Secret` in Development, so leave `MOCK_HOOK_SECRET` empty.

The override file uses the `!override` tag, which needs Docker Compose 2.24 or later.

## The token

The token comes from `mock.env` on the host. Git ignores that file (the root `.gitignore` lists `testing/mock-badge/mock.env`). The page shows only "token configured: yes" or "no". The token leaves the host only in the hook request to the service, over the compose network. The mock logs status codes only. Error text from the service appears on the error page with every occurrence of the token replaced by `[REDACTED]`.

Use a short-lived token and revoke it after testing.
