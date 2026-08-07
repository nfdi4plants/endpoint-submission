namespace WfHubClient

open System.Text.RegularExpressions

/// Recognizing which license an ARC's LICENSE text states.
///
/// WorkflowHub picks up a crate's license only when the root dataset's `license` node is
/// identified by an SPDX URL — neither the license text nor a bare SPDX identifier is read
/// (verified against dev.workflowhub.eu). An unrecognized license therefore has to stay
/// unidentified, so the entry arrives without a license for its owner to set afterwards.
module Licenses =

    // Each rule is an SPDX identifier with the phrases its license text must contain and the
    // phrases that rule it out; the first matching rule wins, so more specific licenses come
    // first. Versioned GNU licenses map to their `-only` identifier: the license file alone
    // grants exactly that version, while "or later" is something a project states in its
    // source headers, and overstating the granted permissions is the worse error.
    let private rules: (string * string list * string list) list =
        [ "Apache-2.0", [ "apache license"; "version 2.0" ], []
          "MIT", [ "permission is hereby granted, free of charge" ], [ "apache license" ]
          "BSD-3-Clause", [ "redistribution and use in source and binary forms"; "neither the name of" ], []
          "BSD-2-Clause", [ "redistribution and use in source and binary forms" ], [ "neither the name of" ]
          "AGPL-3.0-only", [ "gnu affero general public license"; "version 3" ], []
          "LGPL-3.0-only", [ "gnu lesser general public license"; "version 3" ], []
          "GPL-3.0-only", [ "gnu general public license"; "version 3" ], [ "lesser"; "affero" ]
          "GPL-2.0-only", [ "gnu general public license"; "version 2" ], [ "lesser"; "affero" ]
          "MPL-2.0", [ "mozilla public license"; "version 2.0" ], []
          "CC0-1.0", [ "cc0 1.0 universal" ], []
          "CC-BY-SA-4.0", [ "attribution-sharealike 4.0 international" ], []
          "CC-BY-4.0", [ "attribution 4.0 international" ], [ "sharealike" ]
          "Unlicense", [ "this is free and unencumbered software released into the public domain" ], [] ]

    /// The SPDX identifiers this library can recognize.
    let identifiers = rules |> List.map (fun (identifier, _, _) -> identifier)

    /// License texts wrap across lines, so phrases are matched against whitespace-collapsed text.
    let private normalize (text: string) =
        Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim()

    /// The SPDX identifier the license text states, when it is one this library recognizes.
    /// A LICENSE file holding nothing but an identifier is taken at its word.
    let trySpdxIdentifier (licenseText: string) : string option =
        let text = normalize licenseText

        if text = "" then
            None
        else
            match identifiers |> List.tryFind (fun identifier -> identifier.ToLowerInvariant() = text) with
            | Some identifier -> Some identifier
            | None ->
                rules
                |> List.tryFind (fun (_, required, forbidden) ->
                    required |> List.forall text.Contains
                    && forbidden |> List.forall (fun phrase -> not (text.Contains phrase)))
                |> Option.map (fun (identifier, _, _) -> identifier)

    /// The URL WorkflowHub resolves an SPDX identifier by.
    let spdxUrl (identifier: string) = $"https://spdx.org/licenses/{identifier}"
