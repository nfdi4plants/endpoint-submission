module WfHubClient.Tests.PackTests

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open Expecto
open WfHubClient
open WfHubClient.Tests.FixtureArc

let private packFixture (fixture: Fixture) =
    use written = write fixture
    Pack.pack written.Directory

let private item identifier (result: PackResult) =
    match result.Items |> List.tryFind (fun i -> i.Identifier = identifier) with
    | Some found -> found
    | None -> failtestf "no packed item '%s' in %A" identifier (result.Items |> List.map (fun i -> i.Identifier))

let private expectUploadable (packedItem: PackedItem) =
    match packedItem.Status with
    | Uploadable _ -> ()
    | other -> failtestf "expected '%s' to be Uploadable, got %A" packedItem.Identifier other

let private expectNotReady (packedItem: PackedItem) =
    match packedItem.Status with
    | NotReady _ -> ()
    | other -> failtestf "expected '%s' to be NotReady, got %A" packedItem.Identifier other

let private crateBytes (packedItem: PackedItem) =
    match packedItem.Status with
    | Uploadable(bytes, size) ->
        Expect.equal size (int64 bytes.Length) "reported size does not match the crate bytes"
        bytes
    | other -> failtestf "expected '%s' to be Uploadable, got %A" packedItem.Identifier other

let private withZip (packedItem: PackedItem) (check: ZipArchive -> unit) =
    use stream = new MemoryStream(crateBytes packedItem)
    use zip = new ZipArchive(stream, ZipArchiveMode.Read)
    check zip

/// JSON-LD properties hold a single object when there is one value and an array otherwise.
let private asList (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Array -> element.EnumerateArray() |> Seq.toList
    | _ -> [ element ]

let private entryText (zip: ZipArchive) (name: string) =
    match zip.GetEntry name with
    | null -> failtestf "zip has no entry '%s'" name
    | entry ->
        use reader = new StreamReader(entry.Open())
        reader.ReadToEnd()

/// Every fixture with the status each of its items must reach (true = Uploadable,
/// false = NotReady).
let private statusCases =
    [ "valid ARC", Fixtures.validArc, [ "MyWorkflow", true; "MyRun", true ]
      "no title", Fixtures.noTitleWorkflow, [ "MyWorkflow", false; "MyRun", true ]
      "empty title", Fixtures.emptyTitleWorkflow, [ "MyWorkflow", false; "MyRun", true ]
      "title over 255 characters", Fixtures.title256Chars, [ "MyWorkflow", false; "MyRun", true ]
      "no description", Fixtures.noDescriptionWorkflow, [ "MyWorkflow", false; "MyRun", true ]
      "no contact", Fixtures.noContact, [ "MyWorkflow", false; "MyRun", true ]
      "contact without first name", Fixtures.contactWithoutFirstName, [ "MyWorkflow", false; "MyRun", true ]
      "no CWL file", Fixtures.noCwlFileWorkflow, [ "MyWorkflow", false; "MyRun", true ]
      "no URI", Fixtures.noUri, [ "MyWorkflow", false; "MyRun", true ]
      "no version", Fixtures.noVersion, [ "MyWorkflow", false; "MyRun", true ]
      "unnamed component type", Fixtures.unnamedComponentType, [ "MyWorkflow", false; "MyRun", true ]
      "unassigned CWL input", Fixtures.unassignedCwlInput, [ "MyWorkflow", true; "MyRun", false ]
      "mistyped CWL input", Fixtures.mistypedCwlInput, [ "MyWorkflow", true; "MyRun", false ]
      "optional File input with a valid value", Fixtures.optionalFileInput, [ "MyWorkflow", true; "MyRun", true ]
      "CWL input with a default and no value", Fixtures.defaultedInput, [ "MyWorkflow", true; "MyRun", true ]
      "optional CWL input without value or default", Fixtures.optionalUnassignedInput, [ "MyWorkflow", true; "MyRun", true ]
      "mistyped CWL default", Fixtures.mistypedDefault, [ "MyWorkflow", true; "MyRun", false ]
      "no process table", Fixtures.noProcessTable, [ "MyWorkflow", true; "MyRun", false ]
      "blank input cell", Fixtures.blankInputCell, [ "MyWorkflow", true; "MyRun", false ]
      "no output column", Fixtures.noOutputColumn, [ "MyWorkflow", true; "MyRun", false ]
      "multiple failures", Fixtures.multipleFailuresWorkflow, [ "MyWorkflow", false; "MyRun", true ]
      "no license", Fixtures.noLicense, [ "MyWorkflow", false; "MyRun", false ]
      "empty license", Fixtures.emptyLicense, [ "MyWorkflow", false; "MyRun", false ]
      "LFS pointer data file", Fixtures.lfsPointerDataFile, [ "MyWorkflow", true; "MyRun", true ]
      "mixed ARC", Fixtures.mixedArc, [ "MyWorkflow", true; "SecondWorkflow", false; "MyRun", true ] ]

// ARC loading and writing misbehave under concurrent use, so these run sequenced.
[<Tests>]
let tests =
    testSequenced <| testList "pack" [
        testList "statuses" [
            for name, fixture, expectations in statusCases ->
                testCase name <| fun () ->
                    let result = packFixture (fixture ())
                    Expect.isNone result.ArcLoadError "unexpected ARC load error"
                    Expect.hasLength result.Items expectations.Length "unexpected item count"

                    for identifier, uploadable in expectations do
                        let packedItem = item identifier result

                        if uploadable then
                            expectUploadable packedItem
                        else
                            expectNotReady packedItem
        ]

        testCase "packed items carry kind, title and description" <| fun () ->
            let result = packFixture (Fixtures.validArc ())
            let workflow = item "MyWorkflow" result
            Expect.equal workflow.Kind ItemKind.Workflow "wrong kind"
            Expect.equal workflow.Title (Some "My workflow") "wrong title"
            Expect.equal workflow.Description (Some "A workflow that does things.") "wrong description"
            let run = item "MyRun" result
            Expect.equal run.Kind ItemKind.Run "wrong kind"
            Expect.equal run.Title (Some "My run") "wrong title"

        testList "license identification" [
            // WorkflowHub reads a crate's license from this id, and only when it is an SPDX url.
            let licenseIdOf (packedItem: PackedItem) =
                use stream = new MemoryStream(crateBytes packedItem)
                use zip = new ZipArchive(stream, ZipArchiveMode.Read)
                use document = JsonDocument.Parse(entryText zip "ro-crate-metadata.json")

                let root =
                    document.RootElement.GetProperty("@graph").EnumerateArray()
                    |> Seq.find (fun e -> e.GetProperty("@id").GetString() = "./")

                match root.TryGetProperty "license" with
                | true, license -> Some(license.GetProperty("@id").GetString())
                | _ -> None

            testCase "a recognized license is identified by its SPDX url" <| fun () ->
                let mit =
                    "MIT License\n\nCopyright (c) 2026 Somebody\n\nPermission is hereby granted, free of charge, to any person obtaining a copy of this software."

                let result = packFixture { Fixtures.validArc () with License = Some mit }
                Expect.equal result.LicenseSpdxId (Some "MIT") "expected the MIT identifier"

                Expect.equal
                    (licenseIdOf (item "MyWorkflow" result))
                    (Some "https://spdx.org/licenses/MIT")
                    "expected the SPDX url as the crate's license id"

            testCase "an unrecognized license stays unidentified" <| fun () ->
                let result = packFixture (Fixtures.validArc ())
                Expect.isNone result.LicenseSpdxId "expected no identifier"
                Expect.isSome result.License "the license text is still surfaced"

                Expect.equal
                    (licenseIdOf (item "MyWorkflow" result))
                    (Some "LICENSE")
                    "expected the ARC's own license path as the crate's license id"

            testCase "an ARC without a license has no identifier" <| fun () ->
                let result = packFixture (Fixtures.noLicense ())
                Expect.isNone result.LicenseSpdxId "expected no identifier"
                Expect.isNone result.License "expected no license"
        ]

        testCase "license content is surfaced" <| fun () ->
            Expect.equal (packFixture (Fixtures.validArc ())).License (Some licenseText) "expected the license text"
            Expect.isNone (packFixture (Fixtures.noLicense ())).License "expected no license"

            match (packFixture (Fixtures.emptyLicense ())).License with
            | Some content -> Expect.equal (content.Trim()) "" "expected empty license content"
            | None -> failtest "expected an empty license to still be surfaced"

        testCase "a missing or empty license makes every item not ready with the license reason" <| fun () ->
            for fixture, expected in [ Fixtures.noLicense, "no LICENSE file"; Fixtures.emptyLicense, "is empty" ] do
                let result = packFixture (fixture ())
                Expect.hasLength result.Items 2 "unexpected item count"

                for packedItem in result.Items do
                    match packedItem.Status with
                    | NotReady failures ->
                        Expect.exists failures (fun m -> m.Contains expected) $"missing license failure for {packedItem.Identifier}"
                    | other -> failtestf "expected NotReady, got %A" other

        testCase "not-ready reasons are the readiness failures" <| fun () ->
            let result = packFixture (Fixtures.multipleFailuresWorkflow ())

            match (item "MyWorkflow" result).Status with
            | NotReady failures ->
                Expect.hasLength failures 2 "expected both failures"
                Expect.exists failures (fun m -> m.Contains "has no title") "missing title failure"
                Expect.exists failures (fun m -> m.Contains "has no contacts") "missing contacts failure"
            | other -> failtestf "expected NotReady, got %A" other

        testCase "unloadable ARC reports a load error" <| fun () ->
            let dir = Path.Combine(AppContext.BaseDirectory, "fixture-tmp", Guid.NewGuid().ToString "N")
            Directory.CreateDirectory dir |> ignore

            try
                File.WriteAllText(Path.Combine(dir, "isa.investigation.xlsx"), "not a spreadsheet")
                let result = Pack.pack dir
                Expect.isSome result.ArcLoadError "expected a load error"
                Expect.isEmpty result.Items "expected no items"
            finally
                Directory.Delete(dir, true)

        testList "zip assembly" [
            let crate (ids: string list) =
                let parts = ids |> List.map (fun id -> "{\"@id\":\"" + id + "\"}") |> String.concat ","
                "{\"@graph\":[{\"@id\":\"./\",\"hasPart\":[" + parts + "]}]}"

            let withArc (files: string list) (body: string -> unit) =
                let dir = Path.Combine(AppContext.BaseDirectory, "fixture-tmp", Guid.NewGuid().ToString "N")

                try
                    for file in files do
                        let path = Path.Combine(dir, file)
                        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
                        File.WriteAllText(path, file)

                    body dir
                finally
                    Directory.Delete(dir, true)

            let assembled dir ids =
                let json = crate ids
                Pack.assembleZip dir json (Pack.referencedFiles json)

            let entryNames dir ids =
                match assembled dir ids with
                | Ok bytes ->
                    use zip = new ZipArchive(new MemoryStream(bytes))
                    zip.Entries |> Seq.map (fun e -> e.FullName) |> Seq.toList
                | Error message -> failtest message

            testCase "./-prefixed ids become entries without the prefix" <| fun () ->
                withArc [ "a/b.csv" ] (fun dir ->
                    Expect.equal (entryNames dir [ "./a/b.csv" ]) [ "a/b.csv"; "ro-crate-metadata.json" ] "entries")

            testCase "a directory id becomes entries for every file below it" <| fun () ->
                withArc [ "d/one.csv"; "d/sub/two.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "./d" ])
                        [ "d/one.csv"; "d/sub/two.csv"; "ro-crate-metadata.json" ]
                        "entries")

            testCase "a missing id gives an error naming it" <| fun () ->
                withArc [ "a.csv" ] (fun dir ->
                    match assembled dir [ "./a.csv"; "./gone.csv" ] with
                    | Error message -> Expect.stringContains message "gone.csv" "expected the missing path"
                    | Ok _ -> failtest "expected an error")

            testCase "files above the raw size limit fail before any file is read" <| fun () ->
                // The files hold their own names, so "a.csv" has 5 bytes, "d/b.csv" has 7 and the sum is 12.
                withArc [ "a.csv"; "d/b.csv" ] (fun dir ->
                    let json = crate [ "./a.csv"; "./d" ]
                    let limited limit = Pack.assembleZipWithLimit limit dir json (Pack.referencedFiles json)

                    do
                        // An exclusive lock makes any read of a.csv throw, so an Error proves that the
                        // size check ran first.
                        use _locked = new FileStream(Path.Combine(dir, "a.csv"), FileMode.Open, FileAccess.Read, FileShare.None)

                        match limited 11L with
                        | Error message ->
                            Expect.stringContains message "before compression" "the message names the cause"
                            Expect.stringContains message "The service packs at most" "the message names the limit"
                        | Ok _ -> failtest "expected an error for a sum of 12 bytes and a limit of 11"

                    match limited 12L with
                    | Ok _ -> ()
                    | Error message -> failtestf "a sum equal to the limit must pass, got: %s" message)

            testCase "fragment and URL ids produce no entries" <| fun () ->
                withArc [ "a.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "#frag"; "https://example.org/x"; "./a.csv" ])
                        [ "a.csv"; "ro-crate-metadata.json" ]
                        "entries")

            testCase "a file reached directly and through its folder appears once" <| fun () ->
                withArc [ "x/a.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "x/a.csv"; "./x/a.csv"; "./x" ])
                        [ "ro-crate-metadata.json"; "x/a.csv" ]
                        "entries")

            testCase "ids escaping the ARC root give an error naming them" <| fun () ->
                withArc [ "a.csv" ] (fun dir ->
                    let name = Guid.NewGuid().ToString "N" + "-outside"
                    let outside = Path.Combine(dir, "..", name)
                    Directory.CreateDirectory outside |> ignore

                    try
                        let secret = Path.Combine(outside, "secret.csv")
                        File.WriteAllText(secret, "secret")
                        let absolute = Path.GetFullPath(secret).Replace('\\', '/')

                        for id in [ $"../{name}/secret.csv"; absolute; "%2E%2E/" + name + "/secret.csv" ] do
                            match assembled dir [ id ] with
                            | Error message -> Expect.stringContains message id "expected the escaping path"
                            | Ok _ -> failtestf "expected an error for %s" id
                    finally
                        Directory.Delete(outside, true))

            testCase ".git is rejected as an id and skipped inside a folder" <| fun () ->
                withArc [ ".git/config"; "d/a.csv"; "d/.git/x" ] (fun dir ->
                    for id in [ "./.git"; "./.git/config" ] do
                        match assembled dir [ id ] with
                        | Error message -> Expect.stringContains message (id.Substring 2) "expected the .git path"
                        | Ok _ -> failtestf "expected an error for %s" id

                    Expect.equal (entryNames dir [ "./d" ]) [ "d/a.csv"; "ro-crate-metadata.json" ] "entries")

            testCase "ids naming the root give an error" <| fun () ->
                withArc [ "d/a.csv" ] (fun dir ->
                    for id in [ "./d/../"; "d/.." ] do
                        match assembled dir [ id ] with
                        | Error message -> Expect.stringContains message (id.TrimStart('.', '/')) "expected the root path"
                        | Ok _ -> failtestf "expected an error for %s" id)

            testCase "a raw file name with a percent sign is found" <| fun () ->
                withArc [ "50%25.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "./50%25.csv" ])
                        [ "50%25.csv"; "ro-crate-metadata.json" ]
                        "entries")

            testCase "a metadata file on disk never replaces the crate metadata entry" <| fun () ->
                withArc [ "ro-crate-metadata.json"; "d/ro-crate-metadata.json" ] (fun dir ->
                    let ids = [ "./d"; "./d/../ro-crate-metadata.json" ]

                    match assembled dir ids with
                    | Ok bytes ->
                        use zip = new ZipArchive(new MemoryStream(bytes))
                        let names = zip.Entries |> Seq.map (fun e -> e.FullName) |> Seq.toList
                        Expect.equal names [ "d/ro-crate-metadata.json"; "ro-crate-metadata.json" ] "entries"
                        use reader = new StreamReader((zip.GetEntry "ro-crate-metadata.json").Open())
                        Expect.equal (reader.ReadToEnd()) (crate ids) "expected the crate JSON at the root"
                    | Error message -> failtest message)

            testCase "ids with a colon are rejected" <| fun () ->
                withArc [ ".git/config"; "a.csv" ] (fun dir ->
                    for id in [ "./.git::$INDEX_ALLOCATION/config"; "./a.csv:stream" ] do
                        match assembled dir [ id ] with
                        | Error message -> Expect.stringContains message (id.Substring 2) "expected the colon path"
                        | Ok _ -> failtestf "expected an error for %s" id)

            testCase "root and metadata ids produce no extra entries" <| fun () ->
                withArc [ "a.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "."; "./"; "./ro-crate-metadata.json" ])
                        [ "ro-crate-metadata.json" ]
                        "entries")

            testCase "URI scheme ids produce no entries and no error" <| fun () ->
                withArc [ "a.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "doi:10.1000/x"; "urn:uuid:1234"; "mailto:a@b.c" ])
                        [ "ro-crate-metadata.json" ]
                        "entries")

            testCase "a percent-encoded id maps to the decoded file name" <| fun () ->
                withArc [ "a b.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "./a%20b.csv" ])
                        [ "a b.csv"; "ro-crate-metadata.json" ]
                        "entries")

            testCase "non-canonical ids collapse to one canonical entry" <| fun () ->
                withArc [ "x/a.csv" ] (fun dir ->
                    Expect.equal
                        (entryNames dir [ "x//a.csv"; "x/sub/../a.csv"; "./x" ])
                        [ "ro-crate-metadata.json"; "x/a.csv" ]
                        "entries")

            testCase "a link is not followed and rejects its folder" <| fun () ->
                withArc [ "keep.csv"; "outer/inside.csv" ] (fun dir ->
                    let target = Path.Combine(dir, "..", Guid.NewGuid().ToString "N")
                    Directory.CreateDirectory target |> ignore
                    File.WriteAllText(Path.Combine(target, "secret.csv"), "secret")
                    let link = Path.Combine(dir, "outer", "link")

                    try
                        if OperatingSystem.IsWindows() then
                            use proc =
                                System.Diagnostics.Process.Start(
                                    "cmd",
                                    $"/c mklink /J \"{link}\" \"{Path.GetFullPath target}\""
                                )

                            proc.WaitForExit()
                            Expect.equal proc.ExitCode 0 "mklink failed"
                        else
                            Directory.CreateSymbolicLink(link, Path.GetFullPath target) |> ignore

                        match assembled dir [ "./outer/link" ] with
                        | Error message -> Expect.stringContains message "outer/link" "expected the link path"
                        | Ok _ -> failtest "expected an error for the link"

                        match assembled dir [ "./outer" ] with
                        | Error message -> Expect.stringContains message "outer" "expected the folder path"
                        | Ok _ -> failtest "expected an error for the folder containing a link"
                    finally
                        if Directory.Exists link then
                            Directory.Delete link

                        Directory.Delete(target, true))
        ]

        testList "crate zips" [
            testCase "metadata parses and required entries exist" <| fun () ->
                let result = packFixture (Fixtures.validArc ())

                for identifier in [ "MyWorkflow"; "MyRun" ] do
                    withZip (item identifier result) (fun zip ->
                        let metadata = entryText zip "ro-crate-metadata.json"
                        use document = JsonDocument.Parse metadata
                        let graph = document.RootElement.GetProperty("@graph").EnumerateArray() |> Seq.toList

                        let entity id =
                            graph
                            |> List.tryFind (fun e ->
                                match e.TryGetProperty "@id" with
                                | true, v -> v.GetString() = id
                                | _ -> false)
                            |> Option.defaultWith (fun () -> failtestf "crate has no entity '%s'" id)

                        let descriptor = entity "ro-crate-metadata.json"

                        let conformsTo =
                            descriptor.GetProperty "conformsTo"
                            |> asList
                            |> List.map (fun c -> c.GetProperty("@id").GetString())

                        Expect.contains
                            conformsTo
                            "https://w3id.org/workflowhub/workflow-ro-crate/1.0"
                            "expected the Workflow RO-Crate 1.0 profile"

                        // A crate id `./x` names the zip entry `x`.
                        let entryFor (id: string) =
                            zip.GetEntry(if id.StartsWith "./" then id.Substring 2 else id)

                        let root = entity "./"
                        let mainEntity = root.GetProperty("mainEntity").GetProperty("@id").GetString()
                        Expect.isNotNull (box (entryFor mainEntity)) $"mainEntity '{mainEntity}' not in zip"

                        let parts =
                            root.GetProperty "hasPart"
                            |> asList
                            |> List.map (fun p -> p.GetProperty("@id").GetString())
                            |> List.filter (fun id -> not (id.StartsWith "#") && not (id.Contains "://"))

                        Expect.isNonEmpty parts "expected file references in hasPart"

                        for part in parts do
                            Expect.isNotNull (box (entryFor part)) $"hasPart '{part}' not in zip")

            testCase "run crate contains the assay and study files it references" <| fun () ->
                let result = packFixture (Fixtures.validArc ())

                withZip (item "MyRun" result) (fun zip ->
                    Expect.isNotNull
                        (box (zip.GetEntry "assays/MyAssay/dataset/data.csv"))
                        "expected the referenced assay data file in the zip")

            testCase "a CWL default reaches the run crate as the input value" <| fun () ->
                let result = packFixture (Fixtures.defaultedInput ())

                withZip (item "MyRun" result) (fun zip ->
                    use document = JsonDocument.Parse(entryText zip "ro-crate-metadata.json")

                    let hasDefault =
                        document.RootElement.GetProperty("@graph").EnumerateArray()
                        |> Seq.exists (fun e ->
                            let text (name: string) =
                                match e.TryGetProperty name with
                                | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
                                | _ -> null

                            text "name" = "input_file" && text "value" = "hello")

                    Expect.isTrue hasDefault "expected a PropertyValue input_file with value hello")

            testCase "LFS pointer file is included as pointer text" <| fun () ->
                let result = packFixture (Fixtures.lfsPointerDataFile ())

                withZip (item "MyRun" result) (fun zip ->
                    Expect.equal
                        (entryText zip "assays/MyAssay/dataset/data.csv")
                        lfsPointerText
                        "expected the raw pointer text")

            testCase "a workflow file that is still an LFS pointer makes the item fail" <| fun () ->
                let fixture = Fixtures.validArc ()

                let pointerTool =
                    { fixture with
                        ExtraFiles =
                            fixture.ExtraFiles
                            |> List.map (fun (path, content) ->
                                if path = "workflows/MyWorkflow/tool.cwl" then path, lfsPointerText else path, content) }

                match (item "MyWorkflow" (packFixture pointerTool)).Status with
                | PackFailed message ->
                    Expect.stringContains message "Git LFS pointers" "the message names the cause"
                    Expect.stringContains message "workflows/MyWorkflow/tool.cwl" "the message names the file"
                | other -> failtestf "expected PackFailed, got %A" other

            // ARCtrl 3.3.0 stamps `dateCreated` and `datePublished` at every write unless a timestamp
            // is passed, and Pack passes none, so full byte-identity across packs is out of reach here.
            // Everything the assembly controls must be identical: entry set, ordering, timestamps,
            // compression and file contents. The metadata may differ only in those stamps.
            testCase "packing the same directory twice is identical except ARCtrl's date stamps" <| fun () ->
                use written = write (Fixtures.validArc ())
                let first = Pack.pack written.Directory
                let second = Pack.pack written.Directory

                let normalized (json: string) =
                    let replace (name: string) (text: string) =
                        Text.RegularExpressions.Regex.Replace(
                            text,
                            $"\"{name}\": \"[^\"]*\"",
                            $"\"{name}\": \"<normalized>\""
                        )

                    json |> replace "dateCreated" |> replace "datePublished"

                for identifier in [ "MyWorkflow"; "MyRun" ] do
                    withZip (item identifier first) (fun firstZip ->
                        withZip (item identifier second) (fun secondZip ->
                            let names (zip: ZipArchive) =
                                zip.Entries |> Seq.map (fun e -> e.FullName) |> Seq.toList

                            Expect.equal (names secondZip) (names firstZip) "expected identical entry sets"

                            for name in names firstZip do
                                if name = "ro-crate-metadata.json" then
                                    Expect.equal
                                        (normalized (entryText secondZip name))
                                        (normalized (entryText firstZip name))
                                        "expected identical crate metadata modulo date stamps"
                                else
                                    Expect.equal
                                        (entryText secondZip name)
                                        (entryText firstZip name)
                                        $"expected identical content for '{name}'"))

            testCase "referenced file missing from the working tree fails the pack" <| fun () ->
                use written = write (Fixtures.validArc ())
                File.Delete(Path.Combine(written.Directory, "assays", "MyAssay", "dataset", "data.csv"))
                let result = Pack.pack written.Directory
                expectUploadable (item "MyWorkflow" result)

                match (item "MyRun" result).Status with
                | PackFailed message ->
                    Expect.stringContains message "assays/MyAssay/dataset/data.csv" "expected the missing path"
                | other -> failtestf "expected PackFailed, got %A" other

            testCase "crate over the upload limit fails the pack" <| fun () ->
                use written = write (Fixtures.validArc ())
                let oversize = Array.zeroCreate<byte> (105 * 1024 * 1024)
                Random(42).NextBytes oversize
                File.WriteAllBytes(Path.Combine(written.Directory, "assays", "MyAssay", "dataset", "data.csv"), oversize)
                let result = Pack.pack written.Directory
                expectUploadable (item "MyWorkflow" result)

                match (item "MyRun" result).Status with
                | PackFailed message -> Expect.stringContains message "upload limit" "expected the oversize message"
                | other -> failtestf "expected PackFailed, got %A" other
        ]
    ]
