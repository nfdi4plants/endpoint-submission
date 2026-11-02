namespace WfHubClient

open System
open System.IO
open System.IO.Compression
open System.Text.Json
open ARCtrl

module Pack =

    // Two or more scheme characters, so a Windows drive such as `C:/x` is a path.
    let private uriScheme = Text.RegularExpressions.Regex @"^[A-Za-z][A-Za-z0-9+.-]+:"

    /// The paths, relative to the ARC root, that the crate's root dataset references via `hasPart`,
    /// deduplicated. ARCtrl main since PR #615 prefixes data entity ids with `./` and writes them
    /// unencoded. Other writers percent-encode ids, so assembleZip also tries the decoded path.
    /// Fragment ids and ids with a URI scheme name no file, and neither does the root.
    /// `ro-crate-metadata.json` is added separately. A path can name a folder (see `assembleZip`).
    let internal referencedFiles (crateJson: string) =
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
                |> List.filter (fun id -> not (id.StartsWith "#") && not (uriScheme.IsMatch id))
                |> List.map (fun id -> if id.StartsWith "./" then id.Substring 2 else id)
                |> List.filter (fun path ->
                    not (List.contains path [ ""; "."; "./"; "ro-crate-metadata.json" ]))
                |> List.distinct
            | _ -> []

    /// assembleZip adds entries in ordinal path order with fixed timestamps. ARCtrl writes
    /// wall-clock dates into the crate JSON on every write, so two zips of the same ARC contents
    /// differ only where the crate JSON differs.
    /// Each path is a file or a folder under `arcDirectory`. A folder contributes every file below it.
    let internal assembleZip (arcDirectory: string) (crateJson: string) (files: string list) =
        let root = Path.GetFullPath arcDirectory
        // A filesystem root such as `C:\` or `/` already ends with a separator.
        let rootPrefix =
            if Path.EndsInDirectorySeparator root then
                root
            else
                root + string Path.DirectorySeparatorChar

        let isLink (path: string) =
            let info: FileSystemInfo =
                if Directory.Exists path then DirectoryInfo path else FileInfo path

            not (isNull info.LinkTarget) || info.Attributes.HasFlag FileAttributes.ReparsePoint

        let entryName (full: string) = Path.GetRelativePath(root, full).Replace('\\', '/')

        // Paths come from the crate and are user-controlled, so resolve rejects anything outside the
        // ARC root, the root itself, a link anywhere on the path or below a folder, a path under
        // `.git` (which can hold credentials) and any `:`. On Windows a colon opens NTFS stream
        // syntax such as `.git::$INDEX_ALLOCATION`, and ARCtrl treats ids with a colon as URLs. A
        // `.git` folder below a referenced folder is skipped. A rejected path counts as missing.
        let resolveOne (relative: string) =
            let full = Path.GetFullPath(Path.Combine(root, relative))

            let throughLink () =
                let parts =
                    Path.GetRelativePath(root, full).Split(
                        [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]
                    )

                parts
                |> Array.mapi (fun i _ -> Path.Combine(root, String.Join(string Path.DirectorySeparatorChar, parts[..i])))
                |> Array.exists isLink

            let inGit () =
                let first =
                    Path.GetRelativePath(root, full).Split(
                        [| Path.DirectorySeparatorChar; Path.AltDirectorySeparatorChar |]
                    )[0]

                String.Equals(first, ".git", StringComparison.OrdinalIgnoreCase)

            // None means a link was found, so the whole referenced path is rejected.
            let walk (start: string) =
                let options =
                    EnumerationOptions(
                        RecurseSubdirectories = false,
                        IgnoreInaccessible = false,
                        AttributesToSkip = FileAttributes.None
                    )

                let found = ResizeArray()

                // Seq.forall stops at the first link, which rejects the whole folder.
                let rec visit (dir: string) =
                    Directory.EnumerateFileSystemEntries(dir, "*", options)
                    |> Seq.forall (fun entry ->
                        if isLink entry then
                            false
                        elif Directory.Exists entry then
                            String.Equals(Path.GetFileName entry, ".git", StringComparison.OrdinalIgnoreCase)
                            || visit entry
                        else
                            found.Add(entryName entry, entry)
                            true)

                if visit start then Some(List.ofSeq found) else None

            if not (full.StartsWith(rootPrefix, StringComparison.Ordinal)) then
                None
            elif Path.TrimEndingDirectorySeparator full = Path.TrimEndingDirectorySeparator root then
                None
            elif Path.GetRelativePath(root, full).Contains ':' then
                None
            elif not (File.Exists full || Directory.Exists full) || inGit () || throughLink () then
                None
            elif File.Exists full then
                Some [ entryName full, full ]
            else
                // A hasPart id can name a folder (for example a data cell, or a File input whose path
                // is a folder), and an attached crate has to contain its files.
                walk full

        // The raw path comes first because ARCtrl writes ids unencoded. The decoded path covers
        // writers that percent-encode.
        let resolve (relative: string) =
            let exists (path: string) =
                let full = Path.GetFullPath(Path.Combine(root, path))
                File.Exists full || Directory.Exists full

            let decoded = Uri.UnescapeDataString relative

            if not (exists relative) && decoded <> relative then
                resolveOne decoded
            else
                resolveOne relative

        let resolved = files |> List.map (fun file -> file, resolve file)

        let missing =
            resolved |> List.choose (fun (file, found) -> if found.IsNone then Some file else None)

        match missing with
        | _ :: _ ->
            Error
                $"""The crate references file(s) that are missing from the ARC directory or not allowed in a crate (outside the ARC, the ARC root itself, a link, under .git, or containing ':'): {String.concat ", " missing}"""
        | [] ->
            let entries =
                ("ro-crate-metadata.json", Text.Encoding.UTF8.GetBytes crateJson)
                :: (resolved
                    |> List.collect (fun (_, found) -> found.Value)
                    |> List.filter (
                        let seen =
                            Collections.Generic.HashSet<string>(
                                [ "ro-crate-metadata.json" ],
                                if OperatingSystem.IsWindows() then
                                    StringComparer.OrdinalIgnoreCase
                                else
                                    StringComparer.Ordinal
                            )

                        fun (name, _) -> seen.Add name
                    )
                    |> List.map (fun (name, full) -> name, File.ReadAllBytes full))
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
