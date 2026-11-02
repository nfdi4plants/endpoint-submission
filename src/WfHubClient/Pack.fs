namespace WfHubClient

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open ARCtrl

module Pack =

    /// The files the crate's root dataset references via `hasPart`, deduplicated. Fragment ids,
    /// URLs and the root itself are not files; `ro-crate-metadata.json` is added separately.
    let private referencedFiles (crateJson: string) =
        use document = JsonDocument.Parse crateJson

        let ids (element: JsonElement) =
            match element.ValueKind with
            | JsonValueKind.Array -> element.EnumerateArray() |> Seq.toList
            | _ -> [ element ]
            |> List.choose (fun part ->
                match part.TryGetProperty "@id" with
                | true, id -> Option.ofObj (id.GetString())
                | _ -> None)

        let root =
            document.RootElement.GetProperty("@graph").EnumerateArray()
            |> Seq.tryFind (fun entity ->
                match entity.TryGetProperty "@id" with
                | true, id -> id.GetString() = "./"
                | _ -> false)

        match root with
        | None -> []
        | Some rootDataset ->
            match rootDataset.TryGetProperty "hasPart" with
            | true, parts ->
                ids parts
                |> List.filter (fun id ->
                    not (id.StartsWith "#")
                    && not (id.Contains "://")
                    && id <> "./"
                    && id <> "ro-crate-metadata.json")
                |> List.distinct
            | _ -> []

    /// Entries are added in ordinal path order with fixed timestamps, so two zips differ only where
    /// the crate JSON differs. ARCtrl writes wall-clock dates into the crate JSON on every write.
    let private assembleZip (arcDirectory: string) (crateJson: string) (files: string list) =
        let missing =
            files
            |> List.filter (fun file -> not (File.Exists(Path.Combine(arcDirectory, file))))

        match missing with
        | _ :: _ ->
            Error
                $"""The crate references file(s) missing from the ARC directory: {String.concat ", " missing}"""
        | [] ->
            let entries =
                ("ro-crate-metadata.json", Text.Encoding.UTF8.GetBytes crateJson)
                :: (files
                    |> List.map (fun file -> file, File.ReadAllBytes(Path.Combine(arcDirectory, file))))
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

            use buffer = new MemoryStream()

            do
                use zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)

                for name, bytes in entries do
                    let entry = zip.CreateEntry(name, CompressionLevel.Optimal)
                    entry.LastWriteTime <- DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    use stream = entry.Open()
                    stream.Write(bytes, 0, bytes.Length)

            Ok(buffer.ToArray())

    let private packItem
        (arcDirectory: string)
        (kind: ItemKind)
        (identifier: string)
        (title: string option)
        (description: string option)
        (failures: string list)
        (writeCrate: unit -> string)
        : PackedItem =
        let status =
            match failures with
            | _ :: _ -> NotReady failures
            | [] ->
                try
                    let crateJson = writeCrate ()

                    match assembleZip arcDirectory crateJson (referencedFiles crateJson) with
                    | Error error -> PackFailed error
                    | Ok bytes when int64 bytes.Length > WorkflowHubLimits.MaxUploadBytes ->
                        PackFailed
                            $"The finished crate is {bytes.Length} bytes, above WorkflowHub's upload limit of {WorkflowHubLimits.MaxUploadBytes} bytes."
                    | Ok bytes -> Uploadable(bytes, int64 bytes.Length)
                with e ->
                    PackFailed e.Message

        { Identifier = identifier
          Kind = kind
          Title = title
          Description = description
          Status = status }

    /// Loads the ARC at `arcDirectory` and turns every workflow and run into a `PackedItem`:
    /// metadata checks first, then — only for items that pass — a crate write and zip assembly.
    let pack (arcDirectory: string) : PackResult =
        let loaded =
            try
                Ok(ARC.load arcDirectory)
            with e ->
                Error e.Message

        match loaded with
        | Error message ->
            { Items = []
              License = None
              LicenseSpdxId = None
              ArcLoadError = Some message }
        | Ok arc ->
            let spdxId = arc.License |> Option.bind (fun license -> Licenses.trySpdxIdentifier license.Content)

            // The license node's id is what WorkflowHub reads, so a recognized license is
            // identified by its SPDX URL; anything else keeps the ARC's own license path and
            // arrives without a license WorkflowHub can act on.
            let crateLicense =
                match arc.License, spdxId with
                | Some license, Some identifier ->
                    License(license.Type, license.Content, Licenses.spdxUrl identifier)
                | Some license, None -> license
                // No crate is written without a license (see licenseFailures below). This case only
                // keeps the match total.
                | None, _ -> License.GetDefaultLicense()

            // The crate must specify a license and the validation package fails the whole ARC
            // without one, so no item may be uploadable.
            let licenseFailures = Readiness.licenseFailures arc.License

            let workflowItems =
                arc.Workflows
                |> Seq.map (fun workflow ->
                    packItem
                        arcDirectory
                        ItemKind.Workflow
                        workflow.Identifier
                        workflow.Title
                        workflow.Description
                        (Readiness.workflowFailures workflow @ licenseFailures)
                        (fun () ->
                            ARCtrl.Json.ARC.ROCrate.writeWorkflowAsCrate (
                                workflow,
                                arc.FileSystem,
                                crateLicense
                            )))
                |> List.ofSeq

            let runItems =
                arc.Runs
                |> Seq.map (fun run ->
                    packItem
                        arcDirectory
                        ItemKind.Run
                        run.Identifier
                        run.Title
                        run.Description
                        (Readiness.runFailures run @ licenseFailures)
                        (fun () ->
                            ARCtrl.Json.ARC.ROCrate.writeRunAsCrate (run, arc.FileSystem, crateLicense)))
                |> List.ofSeq

            { Items = workflowItems @ runItems
              License = arc.License |> Option.map (fun license -> license.Content)
              LicenseSpdxId = spdxId
              ArcLoadError = None }
