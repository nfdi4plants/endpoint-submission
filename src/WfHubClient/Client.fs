namespace WfHubClient

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// The WorkflowHub HTTP client: upload-only and stateless. Every call except the two OAuth calls
/// is authenticated with the user's OAuth access token, sent as `Authorization: Bearer <token>`.
/// The client never stores a token and never puts one in a result or an error.
module Client =

    /// The shared client has no timeout of its own. Every call sets a limit through its own
    /// cancellation source, so the crate POST can wait longer than the other calls.
    ///
    /// It sends no cookies, because a session cookie from one user's call would authenticate the
    /// next user's call (SEEK checks the session before the token). It follows no redirects,
    /// because the token must only go to the configured instance and a redirect means the API did
    /// not answer as expected. The pooled connections expire so a DNS change reaches a
    /// long-running service.
    let private httpClient =
        new HttpClient(
            new SocketsHttpHandler(
                UseCookies = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes 5.
            ),
            Timeout = Timeout.InfiniteTimeSpan
        )

    /// How long the crate POST may take. A crate can be close to the 99,000,000 byte upload limit,
    /// and WorkflowHub reads and registers it before it answers.
    let UploadTimeout = TimeSpan.FromMinutes 15.0

    /// The limit of every other call.
    let private DefaultTimeout = TimeSpan.FromSeconds 100.0

    let private describe (limit: TimeSpan) =
        if limit.TotalMinutes >= 1.0 && limit.TotalMinutes = Math.Floor limit.TotalMinutes then
            $"{int limit.TotalMinutes} minutes"
        else
            limit.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " seconds"

    let private request (method: HttpMethod) (baseUrl: string) (path: string) (accessToken: string) =
        let message = new HttpRequestMessage(method, Protocol.resolveLink baseUrl path)
        message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", accessToken)
        message

    /// Sends within `limit` and reads the body. `onFailure` maps a connection-level failure to the
    /// error to raise, or None to let the exception pass.
    let private sendWithin
        (limit: TimeSpan)
        (onFailure: exn -> UploadError option)
        (message: HttpRequestMessage)
        : Task<HttpResponseMessage * string> =
        task {
            use deadline = new CancellationTokenSource(limit)

            try
                let! response = httpClient.SendAsync(message, deadline.Token)
                let! body = response.Content.ReadAsStringAsync(deadline.Token)
                return response, body
            with e ->
                match onFailure e with
                | Some error -> return raise (WorkflowHubException(error))
                | None -> return raise e
        }

    /// The mapping of every call except the crate POST: connection failures and timeouts are `Transport`.
    let private send (message: HttpRequestMessage) =
        sendWithin
            DefaultTimeout
            (function
             | :? HttpRequestException as e -> Some(Transport e.Message)
             | :? OperationCanceledException ->
                 Some(Transport $"WorkflowHub did not answer within {describe DefaultTimeout}.")
             | _ -> None)
            message

    let private ensureSuccess (response: HttpResponseMessage) (body: string) =
        if not response.IsSuccessStatusCode then
            raise (WorkflowHubException(Rejected(int response.StatusCode, Protocol.rejectionDetail body), body))

    let private getJson (baseUrl: string) (path: string) (accessToken: string) =
        task {
            use message = request HttpMethod.Get baseUrl path accessToken
            message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
            let! response, body = send message
            ensureSuccess response body
            return body
        }

    /// The OAuth endpoints answer 500 unless the request asks for JSON, so every form POST sends
    /// `Accept: application/json`. A form POST carries no `Authorization` header, because the
    /// token is a form field when it is sent at all.
    let private postForm (baseUrl: string) (path: string) (fields: (string * string) list) =
        task {
            use message = new HttpRequestMessage(HttpMethod.Post, Protocol.resolveLink baseUrl path)
            message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")

            message.Content <-
                new FormUrlEncodedContent(fields |> List.map (fun (name, value) -> KeyValuePair(name, value)))

            return! send message
        }

    /// The string fields of a JSON object body. A body that is not a JSON object has none.
    let private stringFields (body: string) : Map<string, string> =
        try
            use document = JsonDocument.Parse body

            if document.RootElement.ValueKind = JsonValueKind.Object then
                [ for property in document.RootElement.EnumerateObject() do
                      if property.Value.ValueKind = JsonValueKind.String then
                          yield property.Name, property.Value.GetString() ]
                |> Map.ofList
            else
                Map.empty
        with :? JsonException ->
            Map.empty

    /// The `error` and `error_description` fields of an OAuth error body, joined by ": ". Nothing
    /// else from the body is returned, so a proxy page or an echoed request cannot reach the caller.
    let private oauthErrorDetail (body: string) : string =
        let fields = stringFields body

        [ "error"; "error_description" ]
        |> List.choose (fun name -> Map.tryFind name fields)
        |> String.concat ": "

    let private parsed (result: Result<'a, string>) =
        match result with
        | Ok value -> value
        | Error e -> raise (WorkflowHubException(Transport $"Unexpected response body: {e}"))

    /// Multipart crate POST shared by `uploadCrate` and `createVersion`. Deliberately sends no
    /// `Accept` header: it would route the request into SEEK's JSONAPI validator, which
    /// rejects multipart with 422.
    ///
    /// Once the crate has left, a failure can no longer be told from an entry that exists. A
    /// timeout, a dropped connection and a success status with an unreadable body are therefore
    /// `OutcomeUnknown`. Only a failure to resolve the host or to connect, where nothing was sent,
    /// stays `Transport`. A 5xx answer is also `OutcomeUnknown`, because a proxy that gives up
    /// with 502 or 504 leaves SEEK free to finish the entry. A 3xx answer is `OutcomeUnknown` too,
    /// because SEEK's HTML create flow answers with a redirect to the new entry.
    let private postCrate
        (limit: TimeSpan)
        (baseUrl: string)
        (path: string)
        (accessToken: string)
        (crateZip: byte[])
        (teamIds: int list)
        =
        task {
            use message = request HttpMethod.Post baseUrl path accessToken
            use content = new MultipartFormDataContent()
            let zipContent = new ByteArrayContent(crateZip)
            zipContent.Headers.ContentType <- MediaTypeHeaderValue WorkflowHubApi.CrateContentType
            content.Add(zipContent, WorkflowHubApi.CrateFieldName, WorkflowHubApi.CrateFileName)

            for teamId in teamIds do
                content.Add(new StringContent(string teamId), WorkflowHubApi.TeamFieldName)

            message.Content <- content

            let! response, body =
                sendWithin
                    limit
                    (function
                     | :? OperationCanceledException ->
                         Some(OutcomeUnknown $"WorkflowHub did not answer within {describe limit}.")
                     | :? HttpRequestException as e when
                         e.HttpRequestError = HttpRequestError.NameResolutionError
                         || e.HttpRequestError = HttpRequestError.ConnectionError ->
                         Some(Transport e.Message)
                     | :? HttpRequestException as e -> Some(OutcomeUnknown e.Message)
                     | :? IOException as e -> Some(OutcomeUnknown e.Message)
                     | _ -> None)
                    message

            let status = int response.StatusCode

            if status >= 500 then
                raise (
                    WorkflowHubException(
                        OutcomeUnknown $"WorkflowHub answered HTTP {status} after the crate was sent: {Protocol.rejectionDetail body}",
                        body
                    )
                )

            if status >= 300 && status < 400 then
                let location =
                    match response.Headers.Location with
                    | null -> ""
                    | target -> $" to {target}"

                raise (
                    WorkflowHubException(
                        OutcomeUnknown $"WorkflowHub answered HTTP {status} (a redirect){location} after the crate was sent: {Protocol.rejectionDetail body}",
                        body
                    )
                )

            ensureSuccess response body

            match Protocol.parseUploadResponse body with
            | Ok parsedResponse -> return parsedResponse, body
            | Error e ->
                return
                    raise (
                        WorkflowHubException(
                            OutcomeUnknown $"WorkflowHub answered with success, but the answer could not be read: {e}",
                            body
                        )
                    )
        }

    let getCurrentPerson (baseUrl: string, accessToken: string) : Task<PersonInfo> =
        task {
            let! body = getJson baseUrl WorkflowHubApi.CurrentPersonPath accessToken
            return parsed (Protocol.parsePerson body)
        }

    let listTeams (baseUrl: string, accessToken: string) : Task<Team list> =
        task {
            let! person = getCurrentPerson (baseUrl, accessToken)
            let teams = ResizeArray()

            for teamId in person.TeamIds do
                let! body = getJson baseUrl (WorkflowHubApi.projectPath teamId) accessToken
                teams.Add(parsed (Protocol.parseTeam body))

            return List.ofSeq teams
        }

    /// The policy PATCH. The body is JSONAPI. The media type must carry no parameters, so the
    /// content type is set by hand. The caller builds `policyBody`, so it can keep the exact text
    /// that was sent. Returns the raw body answered.
    let private patchPolicy (baseUrl: string, accessToken: string, workflowId: int, policyBody: string) =
        task {
            use message = request HttpMethod.Patch baseUrl (WorkflowHubApi.workflowPath workflowId) accessToken
            message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
            let content = new StringContent(policyBody, Encoding.UTF8)
            content.Headers.ContentType <- MediaTypeHeaderValue "application/vnd.api+json"
            message.Content <- content
            let! response, body = send message
            ensureSuccess response body
            return body
        }

    /// The recovery call when an upload left its entry's visibility unset.
    let setVisibility
        (baseUrl: string, accessToken: string, workflowId: int, visibility: Visibility)
        : Task<unit> =
        task {
            let! _ = patchPolicy (baseUrl, accessToken, workflowId, Protocol.policyBody workflowId visibility)
            ()
        }

    /// `uploadCrate` with an explicit limit for the crate POST, so tests can wait a short time.
    let internal uploadCrateWithin
        (limit: TimeSpan)
        (baseUrl: string, accessToken: string, crateZip: byte[], teamId: int, visibility: Visibility)
        : Task<UploadResult> =
        task {
            let! response, rawBody = postCrate limit baseUrl WorkflowHubApi.WorkflowsPath accessToken crateZip [ teamId ]

            // Built here, so a failed PATCH can report the exact text that was sent.
            let policyRequest = Protocol.policyBody response.Id visibility

            let! policyRawResponse =
                task {
                    try
                        return! patchPolicy (baseUrl, accessToken, response.Id, policyRequest)
                    with :? WorkflowHubException as e ->
                        let detail =
                            match e.Error with
                            | Rejected(status, d) -> $"status {status}: {d}"
                            | Transport message -> message
                            | UploadedButPolicyUnset(_, d) -> d
                            | OutcomeUnknown d -> d

                        // Only an answered request has a body to keep.
                        let policyRawBody =
                            match e.Error with
                            | Rejected _ -> e.RawBody
                            | _ -> ""

                        return
                            raise (
                                WorkflowHubException(
                                    UploadedButPolicyUnset(response.Id, detail),
                                    rawBody,
                                    policyRequest,
                                    policyRawBody
                                )
                            )
                }

            return
                { WorkflowId = response.Id
                  Url = Protocol.resolveLink baseUrl response.SelfLink
                  Version = response.Version
                  Applied = visibility
                  RawResponse = rawBody
                  PolicyRequest = policyRequest
                  PolicyRawResponse = policyRawResponse }
        }

    /// Multipart POST, then a policy PATCH for **both** visibility choices, so the outcome
    /// never depends on server defaults. Between the two calls the entry briefly has SEEK's
    /// default, publicly downloadable policy.
    let uploadCrate
        (baseUrl: string, accessToken: string, crateZip: byte[], teamId: int, visibility: Visibility)
        : Task<UploadResult> =
        uploadCrateWithin UploadTimeout (baseUrl, accessToken, crateZip, teamId, visibility)

    /// Adds a version to an existing entry. Versions inherit the entry's policy, so `Applied`
    /// reports the policy the response exposes (conservatively `Private` when it does not).
    let createVersion
        (baseUrl: string, accessToken: string, workflowId: int, crateZip: byte[])
        : Task<UploadResult> =
        task {
            let! response, rawBody =
                postCrate UploadTimeout baseUrl (WorkflowHubApi.createVersionPath workflowId) accessToken crateZip []

            let applied =
                if response.PolicyAccess = Some WorkflowHubApi.PolicyPublic then Public else Private

            return
                { WorkflowId = response.Id
                  Url = Protocol.resolveLink baseUrl response.SelfLink
                  Version = response.Version
                  Applied = applied
                  RawResponse = rawBody
                  PolicyRequest = ""
                  PolicyRawResponse = "" }
        }

    /// Trades the authorization code for an access token. The client is public, so the PKCE
    /// verifier proves the caller started the flow and no client secret is sent. A refusal carries
    /// the OAuth `error` fields only and never the request. A success answer without an
    /// `access_token` is a `Transport` error with fixed text, because the body holds the token.
    let exchangeAuthorizationCode
        (baseUrl: string, clientId: string, code: string, redirectUri: string, codeVerifier: string)
        : Task<string> =
        task {
            let! response, body =
                postForm
                    baseUrl
                    WorkflowHubApi.TokenPath
                    [ "grant_type", "authorization_code"
                      "code", code
                      "redirect_uri", redirectUri
                      "client_id", clientId
                      "code_verifier", codeVerifier ]

            if not response.IsSuccessStatusCode then
                raise (WorkflowHubException(Rejected(int response.StatusCode, oauthErrorDetail body)))

            match Map.tryFind "access_token" (stringFields body) with
            | Some accessToken when accessToken <> "" -> return accessToken
            | _ -> return raise (WorkflowHubException(Transport "The token answer has no access_token."))
        }

    /// Revokes an access token (RFC 7009). Callers do this once the work that needed the token is done.
    let revokeToken (baseUrl: string, clientId: string, token: string) : Task =
        task {
            let! response, _ = postForm baseUrl WorkflowHubApi.RevokePath [ "token", token; "client_id", clientId ]

            if not response.IsSuccessStatusCode then
                raise (WorkflowHubException(Rejected(int response.StatusCode, "")))
        }
