module WfHubClient.Tests.RequirementTests

open Expecto
open ARCtrl
open WfHubClient
open WfHubClient.Tests.FixtureArc

/// Readiness is always checked against a written-and-reloaded ARC, the same shape `pack` sees.
let private withLoadedArc (fixture: Fixture) (check: ARC -> unit) =
    use written = write fixture
    check (ARC.load written.Directory)

let private workflowFailures (arc: ARC) identifier =
    arc.Workflows
    |> Seq.find (fun w -> w.Identifier = identifier)
    |> Readiness.workflowFailures

let private runFailures (arc: ARC) identifier =
    arc.Runs
    |> Seq.find (fun r -> r.Identifier = identifier)
    |> Readiness.runFailures

let private expectNoFailures (failures: string list) =
    Expect.isEmpty failures "expected the item to pass every readiness requirement"

let private expectSingleFailure (needle: string) (failures: string list) =
    match failures with
    | [ message ] -> Expect.stringContains message needle "unexpected failure message"
    | other -> failtestf "expected exactly one failure containing '%s', got %A" needle other

/// Fixtures whose mutated workflow must report exactly one failure, with the message fragment
/// that identifies the violated requirement; the baseline run must stay failure-free.
let private workflowFailureCases =
    [ "no title", Fixtures.noTitleWorkflow, "has no title"
      "empty title", Fixtures.emptyTitleWorkflow, "has no title"
      "title over 255 characters", Fixtures.title256Chars, "256 characters long"
      "no description", Fixtures.noDescriptionWorkflow, "has no description"
      "no contact", Fixtures.noContact, "has no contacts"
      "contact without first name", Fixtures.contactWithoutFirstName, "have no first name"
      "no CWL file", Fixtures.noCwlFileWorkflow, "has no CWL description"
      "no URI", Fixtures.noUri, "has no URI"
      "no version", Fixtures.noVersion, "has no version"
      "unnamed component type", Fixtures.unnamedComponentType, "without a named component type" ]

/// Same, for fixtures whose mutated run must report exactly one failure.
let private runFailureCases =
    [ "unassigned CWL input", Fixtures.unassignedCwlInput, "assigns no value"
      "mistyped CWL input", Fixtures.mistypedCwlInput, "whose type does not match"
      "no process table", Fixtures.noProcessTable, "has no process table"
      "blank input cell", Fixtures.blankInputCell, "empty input cell"
      "no output column", Fixtures.noOutputColumn, "no output column" ]

// ARC loading and writing misbehave under concurrent use, so these run sequenced.
[<Tests>]
let tests =
    testSequenced <| testList "readiness" [
        testCase "valid workflow and run pass every requirement" <| fun () ->
            withLoadedArc (Fixtures.validArc ()) (fun arc ->
                expectNoFailures (workflowFailures arc "MyWorkflow")
                expectNoFailures (runFailures arc "MyRun"))

        testList "workflow failures" [
            for name, fixture, needle in workflowFailureCases ->
                testCase name <| fun () ->
                    withLoadedArc (fixture ()) (fun arc ->
                        expectSingleFailure needle (workflowFailures arc "MyWorkflow")
                        expectNoFailures (runFailures arc "MyRun"))
        ]

        testList "run failures" [
            for name, fixture, needle in runFailureCases ->
                testCase name <| fun () ->
                    withLoadedArc (fixture ()) (fun arc ->
                        expectSingleFailure needle (runFailures arc "MyRun")
                        expectNoFailures (workflowFailures arc "MyWorkflow"))
        ]

        testCase "multiple failures are all reported" <| fun () ->
            withLoadedArc (Fixtures.multipleFailuresWorkflow ()) (fun arc ->
                let failures = workflowFailures arc "MyWorkflow"
                Expect.hasLength failures 2 "expected both failures"
                Expect.exists failures (fun m -> m.Contains "has no title") "missing title failure"
                Expect.exists failures (fun m -> m.Contains "has no contacts") "missing contacts failure")

        testCase "missing license is a license failure" <| fun () ->
            withLoadedArc (Fixtures.noLicense ()) (fun arc ->
                Expect.isNone arc.License "expected no license"
                expectSingleFailure "no LICENSE file" (Readiness.licenseFailures arc.License))

        testCase "empty license is a license failure" <| fun () ->
            withLoadedArc (Fixtures.emptyLicense ()) (fun arc ->
                Expect.isSome arc.License "expected a LICENSE file"
                expectSingleFailure "is empty" (Readiness.licenseFailures arc.License))

        testCase "license with content passes" <| fun () ->
            withLoadedArc (Fixtures.validArc ()) (fun arc ->
                Expect.isEmpty (Readiness.licenseFailures arc.License) "expected no license failure")

        testCase "mixed ARC reports each item separately" <| fun () ->
            withLoadedArc (Fixtures.mixedArc ()) (fun arc ->
                expectNoFailures (workflowFailures arc "MyWorkflow")
                expectSingleFailure "has no title" (workflowFailures arc "SecondWorkflow")
                expectNoFailures (runFailures arc "MyRun"))
    ]
