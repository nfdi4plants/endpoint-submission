module WfHubClient.Tests.ClientTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open Expecto
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Logging
open WfHubClient

let private token = "secret-bearer-token"

/// Everything a test needs to assert about one request the client sent.
type RecordedRequest =
    { Method: string
      Path: string
      Headers: Map<string, string>
      ContentType: string option
      Body: string
      /// Multipart parts as (field name, filename, content); filename None for plain fields.
      Parts: (string * string option * string) list }

/// A real Kestrel server on a loopback port, because the client's public API takes a base URL.
/// Records every request and answers with whatever `respond` returns for (method, path).
type StubServer(respond: string * string -> int * string) =
    let requests = ConcurrentQueue<RecordedRequest>()

    let app =
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        app.Urls.Add "http://127.0.0.1:0"

        let handler (ctx: HttpContext) : Threading.Tasks.Task =
            task {
                let! parts, body =
                    task {
                        if ctx.Request.HasFormContentType then
                            let! form = ctx.Request.ReadFormAsync()

                            let files =
                                [ for file in form.Files ->
                                      use reader = new StreamReader(file.OpenReadStream())
                                      file.Name, Some file.FileName, reader.ReadToEnd() ]

                            let fields =
                                [ for pair in form do
                                      for value in pair.Value -> pair.Key, None, string value ]

                            return files @ fields, ""
                        else
                            use reader = new StreamReader(ctx.Request.Body)
                            let! body = reader.ReadToEndAsync()
                            return [], body
                    }

                requests.Enqueue
                    { Method = ctx.Request.Method
                      Path = ctx.Request.Path.Value
                      Headers =
                        ctx.Request.Headers
                        |> Seq.map (fun pair -> pair.Key, String.concat "," pair.Value)
                        |> Map.ofSeq
                      ContentType = Option.ofObj ctx.Request.ContentType
                      Body = body
                      Parts = parts }

                let status, responseBody = respond (ctx.Request.Method, ctx.Request.Path.Value)
                ctx.Response.StatusCode <- status
                ctx.Response.ContentType <- "application/json"
                do! ctx.Response.WriteAsync responseBody
            }

        RunExtensions.Run(app, RequestDelegate handler)
        app.StartAsync().GetAwaiter().GetResult()
        app

    member _.BaseUrl = app.Urls |> Seq.head
    member _.Requests = requests |> List.ofSeq

    interface IDisposable with
        member _.Dispose() =
            app.StopAsync().GetAwaiter().GetResult()
            (app :> IAsyncDisposable).DisposeAsync().AsTask().Wait()

let private uploadBody =
    """{ "data": { "id": "42", "type": "workflows",
  "attributes": { "title": "My workflow", "latest_version": 1 },
  "links": { "self": "/workflows/42?version=1" } } }"""

let private errorBody status =
    $"""{{ "errors": [ {{ "title": "failure", "detail": "the server said {status}" }} ] }}"""

let private crate = Encoding.UTF8.GetBytes "not-really-a-zip"

let private expectError (expected: UploadError -> bool) (work: unit -> 'a) =
    try
        work () |> ignore
        failtest "expected a WorkflowHubException"
    with
    | :? WorkflowHubException as e ->
        Expect.isTrue (expected e.Error) $"unexpected error: %A{e.Error}"
        e
    | :? AggregateException as e ->
        match e.InnerException with
        | :? WorkflowHubException as inner ->
            Expect.isTrue (expected inner.Error) $"unexpected error: %A{inner.Error}"
            inner
        | other -> failtestf "expected a WorkflowHubException, got %s" (other.GetType().Name)

[<Tests>]
let tests =
    testList "client" [
        testCase "uploadCrate sends the exact multipart and applies the private policy" <| fun () ->
            use stub =
                new StubServer(function
                    | "POST", "/workflows" -> 201, uploadBody
                    | "PATCH", "/workflows/42" -> 200, uploadBody
                    | _ -> 404, "")

            let result =
                Client.uploadCrate(stub.BaseUrl, token, crate, 5, Private).GetAwaiter().GetResult()

            Expect.equal result.WorkflowId 42 "wrong id"
            Expect.equal result.Url (stub.BaseUrl + "/workflows/42?version=1") "wrong url"
            Expect.equal result.Version 1 "wrong version"
            Expect.equal result.Applied Private "wrong applied visibility"
            Expect.equal result.RawResponse uploadBody "raw response must be verbatim"

            match stub.Requests with
            | [ post; patch ] ->
                Expect.equal post.Method "POST" "wrong method"
                Expect.equal post.Headers.["Authorization"] $"Bearer {token}" "wrong auth header"
                Expect.isFalse (post.Headers.ContainsKey "Accept") "the multipart POST must not send Accept"

                Expect.equal
                    (post.Parts |> List.map (fun (name, _, _) -> name) |> List.sort)
                    [ "ro_crate"; "workflow[project_ids][]" ]
                    "wrong multipart field names"

                Expect.contains post.Parts ("ro_crate", Some "ro-crate.zip", "not-really-a-zip") "wrong crate part"
                Expect.contains post.Parts ("workflow[project_ids][]", None, "5") "wrong team part"

                Expect.equal patch.Method "PATCH" "wrong method"
                Expect.equal patch.Path "/workflows/42" "wrong path"
                Expect.equal patch.ContentType (Some "application/vnd.api+json") "wrong content type (no parameters allowed)"
                Expect.equal patch.Headers.["Authorization"] $"Bearer {token}" "wrong auth header"
                Expect.stringContains patch.Body "\"no_access\"" "expected the private policy"
                Expect.stringContains patch.Body "\"42\"" "expected the workflow id"
            | other -> failtestf "expected POST then PATCH, got %A" (other |> List.map (fun r -> r.Method))

        testCase "uploadCrate PATCHes the download policy for public visibility" <| fun () ->
            use stub =
                new StubServer(function
                    | "POST", "/workflows" -> 201, uploadBody
                    | "PATCH", "/workflows/42" -> 200, uploadBody
                    | _ -> 404, "")

            let result =
                Client.uploadCrate(stub.BaseUrl, token, crate, 5, Public).GetAwaiter().GetResult()

            Expect.equal result.Applied Public "wrong applied visibility"
            let patch = stub.Requests |> List.find (fun r -> r.Method = "PATCH")
            Expect.stringContains patch.Body "\"download\"" "expected the public policy"

        testCase "failing policy PATCH maps to UploadedButPolicyUnset and setVisibility recovers" <| fun () ->
            use stub =
                new StubServer(function
                    | "POST", "/workflows" -> 201, uploadBody
                    | "PATCH", "/workflows/42" -> 500, errorBody 500
                    | _ -> 404, "")

            expectError
                (function
                 | UploadedButPolicyUnset(42, _) -> true
                 | _ -> false)
                (fun () -> Client.uploadCrate(stub.BaseUrl, token, crate, 5, Private).GetAwaiter().GetResult())
            |> ignore

            use recovery = new StubServer(function
                | "PATCH", "/workflows/42" -> 200, uploadBody
                | _ -> 404, "")

            Client.setVisibility(recovery.BaseUrl, token, 42, Private).GetAwaiter().GetResult()
            let patch = recovery.Requests |> List.exactlyOne
            Expect.stringContains patch.Body "\"no_access\"" "expected the private policy"

        testCase "401 and 422 map to Rejected with the JSONAPI detail" <| fun () ->
            for status in [ 401; 422 ] do
                use stub = new StubServer(fun _ -> status, errorBody status)

                expectError
                    (function
                     | Rejected(s, detail) -> s = status && detail.Contains $"the server said {status}"
                     | _ -> false)
                    (fun () -> Client.uploadCrate(stub.BaseUrl, token, crate, 5, Private).GetAwaiter().GetResult())
                |> ignore

        testCase "unreachable host maps to Transport without leaking the token" <| fun () ->
            let e =
                expectError
                    (function
                     | Transport _ -> true
                     | _ -> false)
                    (fun () ->
                        Client.uploadCrate("http://127.0.0.1:1", token, crate, 5, Private).GetAwaiter().GetResult())

            Expect.isFalse (e.Message.Contains token) "the token must not appear in errors"

        testCase "rejection errors do not leak the token" <| fun () ->
            use stub = new StubServer(fun _ -> 401, errorBody 401)

            let e =
                expectError
                    (function
                     | Rejected _ -> true
                     | _ -> false)
                    (fun () -> Client.getCurrentPerson(stub.BaseUrl, token).GetAwaiter().GetResult())

            Expect.isFalse (e.Message.Contains token) "the token must not appear in errors"

        testCase "getCurrentPerson parses id, name and team ids" <| fun () ->
            use stub =
                new StubServer(function
                    | "GET", "/people/current" -> 200, ProtocolTests.personBody
                    | _ -> 404, "")

            let person = Client.getCurrentPerson(stub.BaseUrl, token).GetAwaiter().GetResult()
            Expect.equal person.Id 17 "wrong id"
            Expect.equal person.Name "Ada Lovelace" "wrong name"
            Expect.equal person.TeamIds [ 5; 9 ] "wrong team ids"
            let request = stub.Requests |> List.exactlyOne
            Expect.equal request.Headers.["Accept"] "application/json" "JSON GETs must ask for JSON"

        testCase "listTeams fetches each project of the current person" <| fun () ->
            use stub =
                new StubServer(function
                    | "GET", "/people/current" -> 200, ProtocolTests.personBody
                    | "GET", "/projects/5" ->
                        200, """{ "data": { "id": "5", "attributes": { "title": "Team Five" } } }"""
                    | "GET", "/projects/9" ->
                        200, """{ "data": { "id": "9", "attributes": { "title": "Team Nine" } } }"""
                    | _ -> 404, "")

            let teams = Client.listTeams(stub.BaseUrl, token).GetAwaiter().GetResult()

            Expect.equal
                teams
                [ { Id = 5; Title = "Team Five" }; { Id = 9; Title = "Team Nine" } ]
                "wrong teams"

            Expect.equal
                (stub.Requests |> List.map (fun r -> r.Path))
                [ "/people/current"; "/projects/5"; "/projects/9" ]
                "wrong request sequence"

        testCase "createVersion posts the crate to the version route without a policy PATCH" <| fun () ->
            let versionBody =
                """{ "data": { "id": "42", "type": "workflows",
  "attributes": { "title": "My workflow", "latest_version": 2, "policy": { "access": "download" } },
  "links": { "self": "/workflows/42?version=2" } } }"""

            use stub =
                new StubServer(function
                    | "POST", "/workflows/42/create_version" -> 200, versionBody
                    | _ -> 404, "")

            let result =
                Client.createVersion(stub.BaseUrl, token, 42, crate).GetAwaiter().GetResult()

            Expect.equal result.Version 2 "wrong version"
            Expect.equal result.Applied Public "expected the entry's exposed policy"
            let post = stub.Requests |> List.exactlyOne
            Expect.isFalse (post.Headers.ContainsKey "Accept") "the multipart POST must not send Accept"

            Expect.equal
                (post.Parts |> List.map (fun (name, _, _) -> name))
                [ "ro_crate" ]
                "a version POST carries no project ids"
    ]
