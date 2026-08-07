namespace WfHubClient

open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading.Tasks

/// The WorkflowHub HTTP client: upload-only, stateless, every call authenticated with the
/// bearer token passed in — the token is never stored and never appears in results or errors.
module Client =

    let private httpClient = new HttpClient()

    let private request (method: HttpMethod) (baseUrl: string) (path: string) (bearerToken: string) =
        let message = new HttpRequestMessage(method, Protocol.resolveLink baseUrl path)
        message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", bearerToken)
        message

    /// Sends and reads the body, mapping connection-level failures to `Transport`.
    let private send (message: HttpRequestMessage) : Task<HttpResponseMessage * string> =
        task {
            try
                let! response = httpClient.SendAsync message
                let! body = response.Content.ReadAsStringAsync()
                return response, body
            with
            | :? HttpRequestException as e -> return raise (WorkflowHubException(Transport e.Message))
            | :? TaskCanceledException as e -> return raise (WorkflowHubException(Transport e.Message))
        }

    let private ensureSuccess (response: HttpResponseMessage) (body: string) =
        if not response.IsSuccessStatusCode then
            raise (WorkflowHubException(Rejected(int response.StatusCode, Protocol.rejectionDetail body)))

    let private getJson (baseUrl: string) (path: string) (bearerToken: string) =
        task {
            use message = request HttpMethod.Get baseUrl path bearerToken
            message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
            let! response, body = send message
            ensureSuccess response body
            return body
        }

    let private parsed (result: Result<'a, string>) =
        match result with
        | Ok value -> value
        | Error e -> raise (WorkflowHubException(Transport $"Unexpected response body: {e}"))

    /// Multipart crate POST shared by `uploadCrate` and `createVersion`. Deliberately sends no
    /// `Accept` header: it would route the request into SEEK's JSONAPI validator, which
    /// rejects multipart with 422.
    let private postCrate (baseUrl: string) (path: string) (bearerToken: string) (crateZip: byte[]) (teamIds: int list) =
        task {
            use message = request HttpMethod.Post baseUrl path bearerToken
            use content = new MultipartFormDataContent()
            let zipContent = new ByteArrayContent(crateZip)
            zipContent.Headers.ContentType <- MediaTypeHeaderValue "application/zip"
            content.Add(zipContent, "ro_crate", "ro-crate.zip")

            for teamId in teamIds do
                content.Add(new StringContent(string teamId), "workflow[project_ids][]")

            message.Content <- content
            let! response, body = send message
            ensureSuccess response body
            return parsed (Protocol.parseUploadResponse body), body
        }

    let getCurrentPerson (baseUrl: string, bearerToken: string) : Task<PersonInfo> =
        task {
            let! body = getJson baseUrl WorkflowHubApi.CurrentPersonPath bearerToken
            return parsed (Protocol.parsePerson body)
        }

    let listTeams (baseUrl: string, bearerToken: string) : Task<Team list> =
        task {
            let! person = getCurrentPerson (baseUrl, bearerToken)
            let teams = ResizeArray()

            for teamId in person.TeamIds do
                let! body = getJson baseUrl (WorkflowHubApi.projectPath teamId) bearerToken
                teams.Add(parsed (Protocol.parseTeam body))

            return List.ofSeq teams
        }

    /// The recovery call when an upload left its entry's visibility unset. The PATCH body is
    /// JSONAPI; the media type must carry no parameters, so the content type is set by hand.
    let setVisibility
        (baseUrl: string, bearerToken: string, workflowId: int, visibility: Visibility)
        : Task<unit> =
        task {
            use message = request HttpMethod.Patch baseUrl (WorkflowHubApi.workflowPath workflowId) bearerToken
            message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
            let content = new StringContent(Protocol.policyBody workflowId visibility, Encoding.UTF8)
            content.Headers.ContentType <- MediaTypeHeaderValue "application/vnd.api+json"
            message.Content <- content
            let! response, body = send message
            ensureSuccess response body
        }

    /// Multipart POST, then a policy PATCH for **both** visibility choices, so the outcome
    /// never depends on server defaults. Between the two calls the entry briefly has SEEK's
    /// default, publicly downloadable policy.
    let uploadCrate
        (baseUrl: string, bearerToken: string, crateZip: byte[], teamId: int, visibility: Visibility)
        : Task<UploadResult> =
        task {
            let! response, rawBody = postCrate baseUrl WorkflowHubApi.WorkflowsPath bearerToken crateZip [ teamId ]

            try
                do! setVisibility (baseUrl, bearerToken, response.Id, visibility)
            with :? WorkflowHubException as e ->
                let detail =
                    match e.Error with
                    | Rejected(status, d) -> $"status {status}: {d}"
                    | Transport message -> message
                    | UploadedButPolicyUnset(_, d) -> d

                raise (WorkflowHubException(UploadedButPolicyUnset(response.Id, detail)))

            return
                { WorkflowId = response.Id
                  Url = Protocol.resolveLink baseUrl response.SelfLink
                  Version = response.Version
                  Applied = visibility
                  RawResponse = rawBody }
        }

    /// Adds a version to an existing entry. Versions inherit the entry's policy, so `Applied`
    /// reports the policy the response exposes (conservatively `Private` when it does not).
    let createVersion
        (baseUrl: string, bearerToken: string, workflowId: int, crateZip: byte[])
        : Task<UploadResult> =
        task {
            let! response, rawBody =
                postCrate baseUrl (WorkflowHubApi.createVersionPath workflowId) bearerToken crateZip []

            let applied =
                if response.PolicyAccess = Some WorkflowHubApi.PolicyPublic then Public else Private

            return
                { WorkflowId = response.Id
                  Url = Protocol.resolveLink baseUrl response.SelfLink
                  Version = response.Version
                  Applied = applied
                  RawResponse = rawBody }
        }
