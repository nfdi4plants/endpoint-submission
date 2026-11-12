module WfHubClient.Tests.LiveTests

open System
open System.Net.Http
open System.Net.Http.Headers
open Expecto
open DataHubGit
open WfHubClient
open WfHubClient.Tests.FixtureArc

let private env name =
    match Environment.GetEnvironmentVariable name with
    | null
    | "" -> None
    | value -> Some value

/// All three set ⇒ the live tests run against that instance; otherwise they are skipped.
/// Point WFHUB_BASE_URL at a sandbox (e.g. https://dev.workflowhub.eu), never production.
/// WFHUB_TOKEN is an OAuth access token of that instance with the read and write scopes.
let private settings =
    match env "WFHUB_BASE_URL", env "WFHUB_TOKEN", env "WFHUB_TEAM_ID" with
    | Some baseUrl, Some token, Some teamId -> Some(baseUrl, token, int teamId)
    | _ -> None

let private packWorkflowCrate (fixture: Fixture) =
    use written = write fixture
    let result = Pack.pack written.Directory

    match result.Items |> List.tryPick (fun i -> match i.Status with Uploadable(bytes, _, _) -> Some bytes | _ -> None) with
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

                    let entryPolicyAccess () =
                        use get = authed HttpMethod.Get (WorkflowHubApi.workflowPath result.WorkflowId)
                        let response = http.Send get
                        let body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()

                        match Protocol.parseUploadResponse body with
                        | Ok entry -> entry.PolicyAccess
                        | Error e -> failtestf "could not parse the entry: %s" e

                    Expect.equal (entryPolicyAccess ()) (Some WorkflowHubApi.PolicyPrivate) "the entry must be private"

                    // WorkflowHub answers the crate alone with HTTP 500, so this proves the entry's own
                    // teams are read and sent along.
                    let versioned =
                        Client.createVersion(baseUrl, token, result.WorkflowId, crate).GetAwaiter().GetResult()

                    Expect.equal versioned.WorkflowId result.WorkflowId "a new version must keep the entry id"
                    Expect.equal versioned.Version 2 "expected the second version"

                    Expect.equal
                        (entryPolicyAccess ())
                        (Some WorkflowHubApi.PolicyPrivate)
                        "the entry must still be private after the new version"
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

/// DATAHUB_HOST, DATAHUB_PROJECT_ID and DATAHUB_PAT set (DATAHUB_COMMIT optional) => the checkout
/// test runs against that DataHUB project. It only reads from the project.
let private dataHubSettings =
    match env "DATAHUB_HOST", env "DATAHUB_PROJECT_ID", env "DATAHUB_PAT" with
    | Some host, Some projectId, Some pat -> Some(host, int projectId, pat, env "DATAHUB_COMMIT")
    | _ -> None

let private describe (status: ItemStatus) =
    match status with
    | Uploadable(_, size, _) -> $"uploadable, {size} bytes"
    | NotReady failures -> $"not ready, {failures.Length} failed requirement(s)"
    | PackFailed error -> $"pack failed: {error}"

[<Tests>]
let liveCheckoutTests =
    testSequenced <| testList "live checkout" [
        match dataHubSettings with
        | None ->
            ptestCase "DataHUB checkout and pack (set DATAHUB_HOST, DATAHUB_PROJECT_ID, DATAHUB_PAT to run)" ignore
        | Some(host, projectId, pat, commit) ->
            testCase "checkout with the workflow LFS include, then pack" <| fun () ->
                let checkout =
                    Checkout
                        .checkout(
                            host,
                            projectId,
                            pat,
                            Option.toObj commit,
                            Pack.WorkflowHubLfsInclude,
                            TimeSpan.FromMinutes 5.,
                            Threading.CancellationToken.None
                        )
                        .GetAwaiter()
                        .GetResult()

                let root = IO.Path.GetDirectoryName checkout.Directory

                try
                    Expect.isTrue (IO.Directory.Exists checkout.Directory) "the working tree exists"

                    Expect.isTrue
                        (Text.RegularExpressions.Regex.IsMatch(checkout.CommitSha, "^[0-9a-f]{40}([0-9a-f]{24})?$"))
                        $"expected a hex commit id, got {checkout.CommitSha}"

                    commit
                    |> Option.iter (fun expected ->
                        Expect.equal checkout.CommitSha (expected.ToLowerInvariant()) "the requested commit")

                    for file in IO.Directory.EnumerateFiles(root, "*", IO.SearchOption.AllDirectories) do
                        let text = Text.Encoding.Latin1.GetString(IO.File.ReadAllBytes file)
                        Expect.isFalse (text.Contains pat) $"{file} contains the access token"

                    let result = Pack.pack checkout.Directory
                    result.ArcLoadError |> Option.iter (printfn "ARC load error: %s")

                    for packed in result.Items do
                        printfn "%s: %s" packed.Identifier (describe packed.Status)
                finally
                    (checkout :> IDisposable).Dispose()

                Expect.isFalse (IO.Directory.Exists root) "dispose deletes the temporary root"
    ]
