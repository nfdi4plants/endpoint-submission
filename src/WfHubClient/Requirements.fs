namespace WfHubClient

open ARCtrl

/// The outcome of a single named requirement, evaluated once when the requirement list of a
/// workflow or run is built.
type Requirement =
    { Name: string
      Failure: string option }

module Requirements =

    /// Raised by `fail`; carries the human-readable explanation for the unmet requirement.
    exception CheckFailed of message: string

    let fail (message: string) : unit = raise (CheckFailed message)

    let expect condition message =
        if not condition then fail message

    /// Evaluates `run` immediately; a raised `CheckFailed` (or any other exception) is
    /// recorded as the requirement's failure message.
    let requirement name (run: unit -> unit) =
        try
            run ()
            { Name = name; Failure = None }
        with
        | CheckFailed message -> { Name = name; Failure = Some message }
        | e -> { Name = name; Failure = Some e.Message }

    let holds (r: Requirement) = r.Failure.IsNone

    /// The failure messages of the requirements that did not hold (empty = all passed).
    let failures (requirements: Requirement list) =
        requirements |> List.choose (fun r -> r.Failure)

    /// True when a cell contributes no name to the process input or output it becomes. The first
    /// content entry is the naming one for every cell kind, and ARCtrl happily composes a node
    /// from an empty one, which lands in the crate as an unnamed input or output.
    let cellIsBlank (cell: CompositeCell) =
        match cell.GetContent() with
        | [||] -> true
        | content -> content.[0].Trim() = ""

    /// Positions (1-based) of the persons ARCtrl cannot compose. It builds `creator` from the
    /// first name and fails with "Person must have a given name" when it is missing.
    let personsWithoutFirstName (persons: Person seq) =
        persons
        |> Seq.mapi (fun i p -> i, p)
        |> Seq.filter (fun (_, p) -> p.FirstName |> Option.forall (fun n -> n.Trim() = ""))
        |> Seq.map (fun (i, _) -> string (i + 1))
        |> List.ofSeq
