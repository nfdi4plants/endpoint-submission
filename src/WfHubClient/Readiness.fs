namespace WfHubClient

open ARCtrl
open ARCtrl.CWL
open WfHubClient.Requirements

/// The metadata requirements a workflow or run has to meet before a crate write is attempted.
/// Whether ARCtrl can actually write the crate is deliberately not part of these lists: the
/// write attempt happens afterwards and its failure is reported separately.
module Readiness =

    // Sources:
    //   ARCtrl 3.3.0 src/ARCtrl/ROCrateIO.fs `ROCrate.writeWorkflowAsCrate`, which rejects an
    //   ARC Workflow dataset without identifier/name/description/mainEntity/hasPart/creator and a
    //   workflow protocol without creator/name/input/output/programmingLanguage/url/version/dateCreated.
    //   Of those, only the ones checked below can actually be missing: identifier, mainEntity, hasPart,
    //   programmingLanguage and dateCreated are always populated during composition, and `input`/
    //   `output` are always set even when the CWL declares none, so an empty input or output list is
    //   accepted (verified by running ARCtrl 3.3.0).
    //   ARCtrl 3.3.0 src/ARCtrl/Conversion/Workflow.fs `WorkflowConversion.composeWorkflow`, which
    //   fails on a missing CWL description, untyped inputs/outputs and untyped components. Untyped
    //   inputs/outputs are not checked separately: the CWL decoder already rejects a parameter without
    //   a valid type, so such an ARC fails to load and is reported as an ARC load error instead.
    //   WorkflowHub Workflow RO-Crate profile 1.0 (https://w3id.org/workflowhub/workflow-ro-crate/1.0)
    //   and the WorkflowHub metadata list (https://about.workflowhub.eu/docs/metadata-list/), which
    //   make the crate `name` the mandatory workflow title.
    let workflowRequirements (workflow: ArcWorkflow) =
        let id = workflow.Identifier
        [
            requirement "Title is set" (fun () ->
                expect
                    (workflow.Title |> Option.exists (fun t -> t.Trim() <> ""))
                    $"Workflow '{id}' has no title. ARCtrl writes it as `name` of the ARC Workflow dataset and of the crate root, and WorkflowHub uses `name` as the workflow title, which is mandatory on registration."
            )

            requirement "Title is at most 255 characters" (fun () ->
                match workflow.Title with
                | Some t when t.Length > 255 ->
                    fail $"The title of workflow '{id}' is {t.Length} characters long. WorkflowHub rejects titles longer than 255 characters."
                | _ -> ()
            )

            requirement "Description is set" (fun () ->
                expect
                    (workflow.Description |> Option.exists (fun d -> d.Trim() <> ""))
                    $"Workflow '{id}' has no description. ARCtrl requires `description` on the ARC Workflow dataset before writing the crate, and WorkflowHub shows it as the workflow description."
            )

            requirement "At least one contact" (fun () ->
                expect
                    (workflow.Contacts.Count > 0)
                    $"Workflow '{id}' has no contacts. ARCtrl requires `creator` both on the ARC Workflow dataset and on the workflow protocol, and WorkflowHub reads it as the workflow creators."
            )

            requirement "Every contact has a first name" (fun () ->
                personsWithoutFirstName workflow.Contacts
                |> function
                    | [] -> ()
                    | positions ->
                        fail $"""Contact(s) at position {String.concat ", " positions} of workflow '{id}' have no first name. ARCtrl composes `creator` from it and fails with "Person must have a given name"."""
            )

            requirement "CWL description exists" (fun () ->
                expect
                    workflow.CWLDescription.IsSome
                    $"Workflow '{id}' has no CWL description. ARCtrl cannot compose the workflow protocol without one, and it becomes the crate's `mainEntity`, which the Workflow RO-Crate profile requires. Add a `workflows/{id}/workflow.cwl`."
            )

            requirement "URI is set" (fun () ->
                expect
                    (workflow.URI |> Option.exists (fun u -> u.Trim() <> ""))
                    $"Workflow '{id}' has no URI. ARCtrl only writes `url` on the workflow protocol when the URI is set and rejects the crate without it."
            )

            requirement "Version is set" (fun () ->
                expect
                    (workflow.Version |> Option.exists (fun v -> v.Trim() <> ""))
                    $"Workflow '{id}' has no version. ARCtrl only writes `version` on the workflow protocol when the version is set and rejects the crate without it."
            )

            requirement "All components have a component type with a name" (fun () ->
                workflow.Components
                |> Seq.mapi (fun i c -> i, c)
                |> Seq.filter (fun (_, c) ->
                    match c.ComponentType with
                    | Some ct -> ct.Name.IsNone
                    | None -> true
                )
                |> Seq.map (fun (i, _) -> string i)
                |> List.ofSeq
                |> function
                    | [] -> ()
                    | positions ->
                        fail $"""Workflow '{id}' has components without a named component type at position(s) {String.concat ", " positions}. ARCtrl needs the name to compose the computational tool PropertyValue and fails otherwise."""
            )
        ]

    // Sources:
    //   ARCtrl 3.3.0 src/ARCtrl/ROCrateIO.fs `ROCrate.writeRunAsCrate`, which rejects an ARC Run
    //   dataset without identifier/name/description/about/mentions/creator/hasPart and a workflow
    //   invocation without name/instrument/result/object.
    //   ARCtrl 3.3.0 src/ARCtrl/Conversion/Run.fs
    //   `composeWorkflowInvocationFromArcRun`, which fails on a missing CWL description or an untyped
    //   input parameter. It fails when a required input with no default has no run.yml entry.
    //   In the run crate a missing or null run.yml value takes the CWL default, and an optional
    //   input with neither a run.yml entry nor a default is left out. Both input rules below mirror this,
    //   and the type rule checks run.yml values and substituted defaults with `CWLType.admits`.
    //   Untyped parameters are not checked separately, for the same reason as for workflows: the CWL
    //   decoder rejects them at load time.
    //
    //   ARCtrl validates only the first workflow invocation of a run, so convertibility
    //   depends on process table order and a run without process tables never converts. The
    //   process-table check below requires a fully described first process, which is stricter
    //   than ARCtrl needs. Revisit when ARCtrl changes this.
    let runRequirements (run: ArcRun) =
        let id = run.Identifier
        [
            requirement "Title is set" (fun () ->
                expect
                    (run.Title |> Option.exists (fun t -> t.Trim() <> ""))
                    $"Run '{id}' has no title. ARCtrl writes it as `name` of the ARC Run dataset and of the crate root, and WorkflowHub uses `name` as the title, which is mandatory on registration."
            )

            requirement "Title is at most 255 characters" (fun () ->
                match run.Title with
                | Some t when t.Length > 255 ->
                    fail $"The title of run '{id}' is {t.Length} characters long. WorkflowHub rejects titles longer than 255 characters."
                | _ -> ()
            )

            requirement "Description is set" (fun () ->
                expect
                    (run.Description |> Option.exists (fun d -> d.Trim() <> ""))
                    $"Run '{id}' has no description. ARCtrl requires `description` on the ARC Run dataset before writing the crate, and WorkflowHub shows it as the description."
            )

            requirement "At least one performer" (fun () ->
                expect
                    (run.Performers.Count > 0)
                    $"Run '{id}' has no performers. ARCtrl requires `creator` on the ARC Run dataset, and WorkflowHub reads it as the creators."
            )

            requirement "Every performer has a first name" (fun () ->
                personsWithoutFirstName run.Performers
                |> function
                    | [] -> ()
                    | positions ->
                        fail $"""Performer(s) at position {String.concat ", " positions} of run '{id}' have no first name. ARCtrl composes `creator` from it and fails with "Person must have a given name"."""
            )

            requirement "CWL description exists" (fun () ->
                expect
                    run.CWLDescription.IsSome
                    $"Run '{id}' has no CWL description. ARCtrl cannot compose the workflow protocol without one, and it becomes the crate's `mainEntity`, which the Workflow RO-Crate profile requires. Add a `runs/{id}/run.cwl`."
            )

            requirement "Every CWL input parameter has an assigned value" (fun () ->
                match run.CWLDescription with
                | None -> ()
                | Some pu ->
                    let assigned = run.CWLInput |> Seq.map (fun i -> i.Key) |> Set.ofSeq
                    CWLProcessingUnit.getInputValues pu
                    |> Seq.filter (fun i -> not (assigned.Contains i.Name || i.Optional || i.DefaultValue.IsSome))
                    |> Seq.map (fun i -> i.Name)
                    |> List.ofSeq
                    |> function
                        | [] -> ()
                        | unassigned ->
                            fail $"""Run '{id}' has no value in `runs/{id}/run.yml` for the required CWL input parameter(s) {String.concat ", " unassigned}, and the CWL declares no default for them. ARCtrl builds the workflow invocation from these values and fails when a required parameter has none. Add the values to `runs/{id}/run.yml` or give the parameters a default in the CWL."""
            )

            requirement "Assigned values match the parameter types" (fun () ->
                match run.CWLDescription with
                | None -> ()
                | Some pu ->
                    let assigned = run.CWLInput |> Seq.map (fun v -> v.Key, v) |> Map.ofSeq
                    CWLProcessingUnit.getInputValues pu
                    |> Seq.choose (fun i ->
                        i.Type_
                        |> Option.bind (fun paramType ->
                            // ARCtrl substitutes the default only for a missing or null run.yml value,
                            // so a run.yml value without an inferable type is not checked at all.
                            let effectiveType =
                                match assigned.TryFind i.Name with
                                | Some v when v.Value.IsSome && v.Value.Value <> CWLParameterValue.Null ->
                                    v.Type |> Option.map (fun t -> t, "")
                                | _ ->
                                    i.DefaultValue
                                    |> Option.bind (fun d ->
                                        DecodeParameters.decodeParameterValue d.Node
                                        |> CWLParameterValue.tryInferType
                                        |> Option.map (fun t -> t, " from the CWL default"))

                            effectiveType
                            |> Option.filter (fun (valueType, _) -> not (CWLType.admits paramType valueType))
                            |> Option.map (fun (valueType, origin) ->
                                $"{i.Name}{origin} ({Encode.formatCWLType valueType} vs. {Encode.formatCWLType paramType})"))
                    )
                    |> List.ofSeq
                    |> function
                        | [] -> ()
                        | mismatches ->
                            fail $"""Run '{id}' assigns values whose type does not match the CWL input parameter: {String.concat ", " mismatches}. ARCtrl rejects the conversion on such a mismatch."""
            )

            requirement "First process table has filled input and output columns" (fun () ->
                match run.Tables |> Seq.tryHead with
                | None ->
                    fail $"Run '{id}' has no process table. ARCtrl requires `result` on the workflow invocation, and it is only filled from the outputs of a process table; the invocation composed from the CWL inputs alone carries none."
                | Some table ->
                    // A missing column leaves the invocation without `object`/`result`; a present but
                    // empty cell is worse, because ARCtrl composes an unnamed node from it and writes
                    // the crate as if the process were described.
                    let column (label: string) (isColumn: CompositeHeader -> bool) =
                        match table.Headers |> Seq.tryFindIndex isColumn with
                        | None -> Some $"has no {label} column"
                        | Some col ->
                            [ 0 .. table.RowCount - 1 ]
                            |> List.filter (fun row ->
                                match table.TryGetCellAt(col, row) with
                                | Some cell -> cellIsBlank cell
                                | None -> true
                            )
                            |> function
                                | [] -> None
                                | blank ->
                                    let shown = blank |> List.truncate 5 |> List.map (fun r -> string (r + 1))
                                    let more = if blank.Length > 5 then ", ..." else ""
                                    Some $"""has an empty {label} cell in row(s) {String.concat ", " shown}{more}"""
                    [
                        (if table.RowCount = 0 then Some "has no rows" else None)
                        column "input" (fun h -> h.isInput)
                        column "output" (fun h -> h.isOutput)
                    ]
                    |> List.choose (fun problem -> problem)
                    |> function
                        | [] -> ()
                        | problems ->
                            fail $"""The first process table '{table.Name}' of run '{id}' {String.concat " and " problems}. ARCtrl composes the run's workflow invocation from this table's rows and validates only the first one, so the table has to name the process inputs (`object`) and outputs (`result`); later tables are not considered."""
            )
        ]

    /// The metadata failure messages of a workflow (empty = ready for the crate write attempt).
    let workflowFailures (workflow: ArcWorkflow) =
        workflowRequirements workflow |> failures

    /// The metadata failure messages of a run (empty = ready for the crate write attempt).
    let runFailures (run: ArcRun) =
        runRequirements run |> failures

    // Workflow RO-Crate profile 1.0: "The Crate MUST specify a license."
    // ARCtrl does not enforce this: without a LICENSE file it substitutes the placeholder
    // "ALL RIGHTS RESERVED BY THE AUTHORS", which is not one of the licenses WorkflowHub accepts.
    /// The failure messages for the ARC's license (empty = a non-empty LICENSE file exists).
    let licenseFailures (license: License option) =
        match license with
        | None ->
            [ """The ARC has no LICENSE file. The Workflow RO-Crate profile 1.0 requires the crate to specify a license, and without one ARCtrl writes the placeholder "ALL RIGHTS RESERVED BY THE AUTHORS" instead. Add a LICENSE, LICENSE.txt, LICENSE.md or LICENSE.rst at the ARC root.""" ]
        | Some lic when lic.Content.Trim() = "" ->
            [ $"The LICENSE file '{lic.Path}' of this ARC is empty. Its text is what ARCtrl writes into the crate as the license, so it has to state the license the workflow is published under." ]
        | Some _ -> []
