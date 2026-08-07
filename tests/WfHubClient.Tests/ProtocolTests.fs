module WfHubClient.Tests.ProtocolTests

open Expecto
open WfHubClient

// Bodies mirror SEEK's JSONAPI format: string ids, attributes.title, instance-relative
// links.self, error objects under `errors`.
let personBody =
    """{
  "data": {
    "id": "17",
    "type": "people",
    "attributes": { "title": "Ada Lovelace", "first_name": "Ada", "last_name": "Lovelace" },
    "relationships": {
      "projects": { "data": [ { "id": "5", "type": "projects" }, { "id": "9", "type": "projects" } ] }
    }
  }
}"""

let private projectBody =
    """{
  "data": {
    "id": "5",
    "type": "projects",
    "attributes": { "title": "My Team" }
  }
}"""

let private uploadBody =
    """{
  "data": {
    "id": "42",
    "type": "workflows",
    "attributes": { "title": "My workflow", "latest_version": 1 },
    "links": { "self": "/workflows/42?version=1" }
  }
}"""

let private errorBody =
    """{
  "errors": [
    { "title": "Unprocessable Entity", "detail": "RO-Crate could not be processed" },
    { "detail": "project_ids is missing" }
  ]
}"""

[<Tests>]
let tests =
    testList "protocol" [
        testCase "person body parses with team ids" <| fun () ->
            match Protocol.parsePerson personBody with
            | Ok person ->
                Expect.equal person.Id 17 "wrong id"
                Expect.equal person.Name "Ada Lovelace" "wrong name"
                Expect.equal person.TeamIds [ 5; 9 ] "wrong team ids"
            | Error e -> failtestf "parse failed: %s" e

        testCase "person body without projects yields no team ids" <| fun () ->
            let body = """{ "data": { "id": "17", "attributes": { "title": "Ada" } } }"""

            match Protocol.parsePerson body with
            | Ok person -> Expect.isEmpty person.TeamIds "expected no team ids"
            | Error e -> failtestf "parse failed: %s" e

        testCase "project body parses as team" <| fun () ->
            match Protocol.parseTeam projectBody with
            | Ok team -> Expect.equal team { Id = 5; Title = "My Team" } "wrong team"
            | Error e -> failtestf "parse failed: %s" e

        testCase "upload body parses id, link and version" <| fun () ->
            match Protocol.parseUploadResponse uploadBody with
            | Ok response ->
                Expect.equal response.Id 42 "wrong id"
                Expect.equal response.SelfLink "/workflows/42?version=1" "wrong link"
                Expect.equal response.Version 1 "wrong version"
            | Error e -> failtestf "parse failed: %s" e

        testCase "upload body without data is an error" <| fun () ->
            Expect.isError (Protocol.parseUploadResponse """{ "unrelated": true }""") "expected a parse error"

        testCase "rejection detail prefers JSONAPI error messages" <| fun () ->
            Expect.equal
                (Protocol.rejectionDetail errorBody)
                "RO-Crate could not be processed; project_ids is missing"
                "expected the joined details"

        testCase "rejection detail falls back to the raw body" <| fun () ->
            Expect.equal (Protocol.rejectionDetail "plain text") "plain text" "expected the raw body"

        testCase "policy body carries the SEEK access value" <| fun () ->
            let body = Protocol.policyBody 42 Private
            Expect.stringContains body "\"no_access\"" "expected no_access"
            Expect.stringContains body "\"42\"" "expected the workflow id"
            Expect.stringContains body "\"workflows\"" "expected the JSONAPI type"
            Expect.stringContains (Protocol.policyBody 42 Public) "\"download\"" "expected download"

        testCase "relative links resolve against the base url" <| fun () ->
            Expect.equal
                (Protocol.resolveLink "https://dev.workflowhub.eu" "/workflows/42?version=1")
                "https://dev.workflowhub.eu/workflows/42?version=1"
                "wrong resolution"

            Expect.equal
                (Protocol.resolveLink "https://dev.workflowhub.eu/" "workflows/42")
                "https://dev.workflowhub.eu/workflows/42"
                "wrong resolution without slashes"

            Expect.equal
                (Protocol.resolveLink "https://dev.workflowhub.eu" "https://elsewhere.example/x")
                "https://elsewhere.example/x"
                "absolute links must pass through"
    ]
