namespace WfHubClient

open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Text.Json
open ARCtrl

module Pack =

    /// Hydrates the workflow folder only. Run crates also ship files from there, and a pointer
    /// would break them. Everything outside the hydrated paths stays a pointer. Callers pass it to
    /// `DataHubGit.Checkout.checkout` as the LFS include list.
    let WorkflowHubLfsInclude: IReadOnlyList<string> =
        Array.AsReadOnly [| "workflows/**" |] :> IReadOnlyList<string>

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

    let private pointerOid = Text.RegularExpressions.Regex @"^oid sha256:[0-9a-f]{64}$"
    let private pointerSize = Text.RegularExpressions.Regex @"^size [0-9]+$"

    /// A git LFS pointer is a regular file of at most 1024 bytes that starts with the spec line and
    /// carries an `oid sha256:` line and a `size` line. A checkout without LFS smudging leaves
    /// these in place of the real content.
    let internal isLfsPointer (path: string) =
        let info = FileInfo path

        if not info.Exists || info.Length > 1024L then
            false
        else
            let lines = (File.ReadAllText path).Split('\n') |> Array.map (fun line -> line.TrimEnd '\r')

            lines.Length > 0
            && lines[0].StartsWith("version https://git-lfs.github.com/spec/v1", StringComparison.Ordinal)
            && lines |> Array.exists pointerOid.IsMatch
            && lines |> Array.exists pointerSize.IsMatch

    /// The most file content, in bytes before compression, that one crate may hold. It is twice
    /// the upload limit, because text files compress well. A larger sum fails
    /// the item before any file is read, so one huge file cannot exhaust the service's memory.
    let internal MaxRawCrateBytes = 2L * WorkflowHubLimits.MaxUploadBytes

    /// Whole decimal megabytes (10^6 bytes), the unit of the upload limit. A sum rounds up and a
    /// limit rounds down, so a sum above the limit never reads as equal to it.
    let private megabytesUp (bytes: int64) = (bytes + 999_999L) / 1_000_000L
    let private megabytesDown (bytes: int64) = bytes / 1_000_000L

    /// assembleZip adds entries in ordinal path order with fixed timestamps. ARCtrl writes
    /// wall-clock dates into the crate JSON on every write, so two zips of the same ARC contents
    /// differ only where the crate JSON differs.
    /// Each path is a file or a folder under `arcDirectory`. A folder contributes every file below it.
    /// The files must add up to `maxRawBytes` or less. The sizes come from the file system, and
    /// the check runs before any file is read.
    let internal assembleZipWithLimit
        (maxRawBytes: int64)
        (arcDirectory: string)
        (crateJson: string)
        (files: string list)
        =
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

        // Only valid once `missing` is empty, because it reads `found.Value`.
        let uniqueFiles () =
            resolved
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

        // Workflow files that are still pointers would ship as placeholders. Pointers outside
        // workflows/ are expected, because a checkout hydrates only that folder.
        let pointerEntries () =
            uniqueFiles ()
            |> List.filter (fun (name, full) ->
                (name.StartsWith("workflows/", StringComparison.Ordinal) || name.EndsWith(".cwl", StringComparison.Ordinal))
                && isLfsPointer full)
            |> List.map fst

        // The size of every file the zip will hold, taken from the file system without reading
        // any content. Only valid once `missing` is empty, like `uniqueFiles`.
        let rawBytes () =
            uniqueFiles () |> List.sumBy (fun (_, full) -> FileInfo(full).Length)

        match missing with
        | _ :: _ ->
            Error
                $"""The crate references file(s) that are missing from the ARC directory or not allowed in a crate (outside the ARC, the ARC root itself, a link, under .git, or containing ':'): {String.concat ", " missing}"""
        | [] when not (List.isEmpty (pointerEntries ())) ->
            Error
                $"""These workflow files are Git LFS pointers whose content was not downloaded, so the crate would ship placeholders instead of the files: {String.concat ", " (pointerEntries ())}"""
        | [] when rawBytes () > maxRawBytes ->
            Error
                $"The files of this item add up to {megabytesUp (rawBytes ())} MB before compression. The service packs at most {megabytesDown maxRawBytes} MB of files into one crate."
        | [] ->
            let entries =
                ("ro-crate-metadata.json", Text.Encoding.UTF8.GetBytes crateJson)
                :: (uniqueFiles () |> List.map (fun (name, full) -> name, File.ReadAllBytes full))
                |> List.sortWith (fun (a, _) (b, _) -> String.CompareOrdinal(a, b))

            use buffer = new MemoryStream()

            do
                use zip = new ZipArchive(buffer, ZipArchiveMode.Create, true)

                for name, bytes in entries do
                    let entry = zip.CreateEntry(name, CompressionLevel.Optimal)
                    entry.LastWriteTime <- DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    use stream = entry.Open()
                    stream.Write(bytes, 0, bytes.Length)

            // Workflow pointers failed the item above, so these are data files outside workflows/.
            let pointers =
                uniqueFiles ()
                |> List.filter (fun (_, full) -> isLfsPointer full)
                |> List.map fst
                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))

            Ok(buffer.ToArray(), pointers)

    /// `assembleZipWithLimit` with the service's limit, `MaxRawCrateBytes`.
    let internal assembleZip (arcDirectory: string) (crateJson: string) (files: string list) =
        assembleZipWithLimit MaxRawCrateBytes arcDirectory crateJson files

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
                    | Ok(bytes, _) when int64 bytes.Length > WorkflowHubLimits.MaxUploadBytes ->
                        PackFailed
                            $"The finished crate is {bytes.Length} bytes, above the upload limit of {WorkflowHubLimits.MaxUploadBytes} bytes."
                    | Ok(bytes, pointers) -> Uploadable(bytes, int64 bytes.Length, pointers)
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
