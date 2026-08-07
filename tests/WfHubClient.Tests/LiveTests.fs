module WfHubClient.Tests.LiveTests

open System
open System.Net.Http
open System.Net.Http.Headers
open Expecto
open WfHubClient
open WfHubClient.Tests.FixtureArc

let private env name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

/// All three set ⇒ the live tests run against that instance; otherwise they are skipped.
/// Point WFHUB_BASE_URL at a sandbox (e.g. https://dev.workflowhub.eu), never production.
let private settings =
    match env "WFHUB_BASE_URL", env "WFHUB_TOKEN", env "WFHUB_TEAM_ID" with
    | Some baseUrl, Some token, Some teamId -> Some(baseUrl, token, int teamId)
    | _ -> None

let private packWorkflowCrate (fixture: Fixture) =
    use written = write fixture
    let result = Pack.pack written.Directory

    match result.Items |> List.tryPick (fun i -> match i.Status with Uploadable(bytes, _) -> Some bytes | _ -> None) with
    | Some bytes -> bytes, result.LicenseSpdxId
    | None -> failtest "the fixture must produce an uploadable crate"

let private packedWorkflowCrate () = fst (packWorkflowCrate (Fixtures.validArc ()))

let private mitLicenseText =
    "MIT License\n\nCopyright (c) 2026 Somebody\n\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the \"Software\"), to deal in the Software without restriction."

[<Tests>]
let tests =
    testSequenced <| testList "live" [
        match settings with
        | None ->
            ptestCase "dev instance round trip (set WFHUB_BASE_URL, WFHUB_TOKEN, WFHUB_TEAM_ID to run)" ignore
        | Some(baseUrl, token, teamId) ->
            testCase "the configured team is among the current person's teams" <| fun () ->
                let teams = Client.listTeams(baseUrl, token).GetAwaiter().GetResult()
                Expect.exists teams (fun t -> t.Id = teamId) $"team {teamId} not in %A{teams}"

            testCase "private upload round trip" <| fun () ->
                let crate = packedWorkflowCrate ()

                let result =
                    Client.uploadCrate(baseUrl, token, crate, teamId, Private).GetAwaiter().GetResult()

                use http = new HttpClient()

                let authed (method: HttpMethod) (path: string) =
                    let message = new HttpRequestMessage(method, Protocol.resolveLink baseUrl path)
                    message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                    message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
                    message

                try
                    Expect.isTrue (result.WorkflowId > 0) "expected a workflow id"
                    Expect.stringStarts result.Url (baseUrl.TrimEnd '/') "url must resolve against the base url"
                    Expect.equal result.Applied Private "expected the private policy"

                    use get = authed HttpMethod.Get (WorkflowHubApi.workflowPath result.WorkflowId)
                    let response = http.Send get
                    let body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

                    match Protocol.parseUploadResponse body with
                    | Ok entry ->
                        Expect.equal entry.PolicyAccess (Some WorkflowHubApi.PolicyPrivate) "the entry must be private"
                    | Error e -> failtestf "could not parse the entry: %s" e
                finally
                    // The library is upload-only by design; the test harness deletes directly.
                    use delete = authed HttpMethod.Delete (WorkflowHubApi.workflowPath result.WorkflowId)
                    http.Send delete |> ignore

            // WorkflowHub only reads a license identified by its SPDX url, so this is what
            // proves the mapping actually reaches the registered entry.
            testCase "a recognized license arrives as its SPDX identifier" <| fun () ->
                let crate, spdxId = packWorkflowCrate { Fixtures.validArc () with License = Some mitLicenseText }
                Expect.equal spdxId (Some "MIT") "the fixture license must be recognized before uploading"

                let result =
                    Client.uploadCrate(baseUrl, token, crate, teamId, Private).GetAwaiter().GetResult()

                use http = new HttpClient()

                let authed (method: HttpMethod) (path: string) =
                    let message = new HttpRequestMessage(method, Protocol.resolveLink baseUrl path)
                    message.Headers.Authorization <- AuthenticationHeaderValue("Bearer", token)
                    message.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
                    message

                try
                    use get = authed HttpMethod.Get (WorkflowHubApi.workflowPath result.WorkflowId)
                    let body = (http.Send get).Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    use document = System.Text.Json.JsonDocument.Parse body

                    let license =
                        document.RootElement
                            .GetProperty("data")
                            .GetProperty("attributes")
                            .GetProperty("license")
                            .GetString()

                    Expect.equal license "MIT" "WorkflowHub must report the mapped license"
                finally
                    use delete = authed HttpMethod.Delete (WorkflowHubApi.workflowPath result.WorkflowId)
                    http.Send delete |> ignore
    ]
