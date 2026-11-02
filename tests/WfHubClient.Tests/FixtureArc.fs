module WfHubClient.Tests.FixtureArc

open System
open System.IO
open ARCtrl

/// Raw file contents ARCtrl does not serialize from the in-memory ARC object; they are
/// overlaid onto the written directory so `ARC.load` picks them up.
module Cwl =

    let workflowCwl = """cwlVersion: v1.2
class: Workflow
inputs:
  input_file:
    type: File
outputs:
  result:
    type: File
    outputSource: step1/result
steps:
  step1:
    run: tool.cwl
    in:
      input_file: input_file
    out: [result]
"""

    let toolCwl = """cwlVersion: v1.2
class: CommandLineTool
baseCommand: echo
inputs:
  input_file:
    type: File
outputs:
  result:
    type: File
    outputBinding:
      glob: result.csv
"""

    let runCwl = """cwlVersion: v1.2
class: CommandLineTool
baseCommand: echo
inputs:
  input_file:
    type: File
outputs:
  result:
    type: File
    outputBinding:
      glob: result.csv
"""

    /// Job file assigning the run's CWL input; `../../` paths are rewritten to ARC-root-relative
    /// paths in the emitted crate.
    let runYml = """input_file:
  class: File
  path: ../../assays/MyAssay/dataset/data.csv
"""

let licenseText = "This fixture is published under the MIT license."

/// The content of a git LFS pointer file, as it sits in a working tree cloned without smudging.
let lfsPointerText =
    "version https://git-lfs.github.com/spec/v1\noid sha256:4d7a214614ab2935c943f9e0ff69d22eadbb8f32b1258daaa5e2ca24d17e2393\nsize 12345\n"

/// An in-memory ARC plus the raw files to overlay after writing it. The in-memory objects only
/// carry what ARCtrl serializes (xlsx metadata, LICENSE); CWL descriptions, job files and data
/// files exist as overlay entries and reach the loaded ARC from disk.
type Fixture =
    { Workflows: ArcWorkflow list
      Runs: ArcRun list
      License: string option
      ExtraFiles: (string * string) list }

/// A fixture ARC written to a throwaway directory.
type FixtureArc =
    { Directory: string }

    interface IDisposable with
        member this.Dispose() =
            try
                Directory.Delete(this.Directory, true)
            with _ ->
                ()

/// The standard overlay set matching the baseline identifiers `MyWorkflow` / `MyRun`.
let baselineExtraFiles =
    [ "workflows/MyWorkflow/workflow.cwl", Cwl.workflowCwl
      "workflows/MyWorkflow/tool.cwl", Cwl.toolCwl
      "runs/MyRun/run.cwl", Cwl.runCwl
      "runs/MyRun/run.yml", Cwl.runYml
      "assays/MyAssay/dataset/data.csv", "a,b\n1,2\n"
      "runs/MyRun/result.csv", "c\n3\n" ]

/// A workflow satisfying every readiness requirement (once its CWL overlay is on disk).
let validWorkflow () =
    ArcWorkflow(
        "MyWorkflow",
        title = "My workflow",
        description = "A workflow that does things.",
        uri = "https://git.example.org/my/repo",
        version = "1.0.0",
        contacts = ResizeArray [ Person(firstName = "Ada", lastName = "Lovelace") ]
    )

/// A process table with one row of filled input and output columns.
let validRunTable () =
    let table = ArcTable.init "Process"

    table.AddColumn(
        CompositeHeader.Input IOType.Data,
        ResizeArray [ CompositeCell.createData (Data(name = "assays/MyAssay/dataset/data.csv")) ]
    )

    table.AddColumn(
        CompositeHeader.Output IOType.Data,
        ResizeArray [ CompositeCell.createData (Data(name = "runs/MyRun/result.csv")) ]
    )

    table

/// A run satisfying every readiness requirement (once its CWL/job overlays are on disk).
let validRun () =
    ArcRun(
        "MyRun",
        title = "My run",
        description = "A run that ran.",
        workflowIdentifiers = ResizeArray [ "MyWorkflow" ],
        tables = ResizeArray [ validRunTable () ],
        performers = ResizeArray [ Person(firstName = "Grace", lastName = "Hopper") ]
    )

/// Writes the fixture as a real ARC directory under the test assembly's `fixture-tmp` folder;
/// dispose the result to delete it.
let write (fixture: Fixture) : FixtureArc =
    let dir =
        Path.Combine(AppContext.BaseDirectory, "fixture-tmp", Guid.NewGuid().ToString "N")

    let arc =
        ARC(
            "FixtureArc",
            workflows = ResizeArray fixture.Workflows,
            runs = ResizeArray fixture.Runs
        )

    fixture.License |> Option.iter arc.SetLicenseFulltext
    arc.Write dir

    // A fixture without a license means no LICENSE file at all, whatever the writer defaults to.
    if fixture.License.IsNone then
        let licensePath = Path.Combine(dir, "LICENSE")

        if File.Exists licensePath then
            File.Delete licensePath

    for relativePath, content in fixture.ExtraFiles do
        let path = Path.Combine(dir, relativePath)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, content)

    { Directory = dir }

/// One function per readiness scenario. Each starts from the fully valid workflow/run pair —
/// license and overlay files included — and mutates exactly the one thing its name describes,
/// leaving everything else at its baseline value.
module Fixtures =

    let private baseline () : Fixture =
        { Workflows = [ validWorkflow () ]
          Runs = [ validRun () ]
          License = Some licenseText
          ExtraFiles = baselineExtraFiles }

    let validArc () = baseline ()

    let noTitleWorkflow () =
        let fixture = baseline ()
        fixture.Workflows.Head.Title <- None
        fixture

    let emptyTitleWorkflow () =
        let fixture = baseline ()
        fixture.Workflows.Head.Title <- Some "   "
        fixture

    let title256Chars () =
        let fixture = baseline ()
        fixture.Workflows.Head.Title <- Some(String.replicate 256 "a")
        fixture

    let noDescriptionWorkflow () =
        let fixture = baseline ()
        fixture.Workflows.Head.Description <- None
        fixture

    let noContact () =
        let fixture = baseline ()
        fixture.Workflows.Head.Contacts <- ResizeArray []
        fixture

    let contactWithoutFirstName () =
        let fixture = baseline ()
        fixture.Workflows.Head.Contacts <- ResizeArray [ Person(lastName = "Lovelace") ]
        fixture

    let noCwlFileWorkflow () =
        let fixture = baseline ()

        { fixture with
            ExtraFiles =
                fixture.ExtraFiles
                |> List.filter (fun (path, _) -> path <> "workflows/MyWorkflow/workflow.cwl") }

    let noUri () =
        let fixture = baseline ()
        fixture.Workflows.Head.URI <- None
        fixture

    let noVersion () =
        let fixture = baseline ()
        fixture.Workflows.Head.Version <- None
        fixture

    /// `Component.create` takes its arguments the same way the constructors elsewhere in this
    /// file do (bare values, omitted for `None`); the type's own constructor is private. An
    /// `OntologyAnnotation` with a term source and accession but no `Name` survives the
    /// isa.workflow.xlsx round trip still unnamed (verified against ARCtrl 3.2.0).
    let unnamedComponentType () =
        let fixture = baseline ()
        let componentType = OntologyAnnotation(tsr = "EDAM", tan = "EDAM:00000000")

        fixture.Workflows.Head.Components <-
            ResizeArray [ ARCtrl.Process.Component.create (componentType = componentType) ]

        fixture

    let unassignedCwlInput () =
        let fixture = baseline ()

        { fixture with
            ExtraFiles = fixture.ExtraFiles |> List.filter (fun (path, _) -> path <> "runs/MyRun/run.yml") }

    /// The `class: Directory` value in `run.yml` shows the type Directory, which the `File` input
    /// `input_file` of `run.cwl` does not admit.
    let mistypedCwlInput () =
        let fixture = baseline ()

        { fixture with
            ExtraFiles =
                fixture.ExtraFiles
                |> List.map (fun (path, content) ->
                    if path = "runs/MyRun/run.yml" then
                        path, "input_file:\n  class: Directory\n  path: ../../assays/MyAssay/dataset\n"
                    else
                        path, content) }

    /// Replaces `run.cwl` and, when `runYml` is `None`, drops `run.yml`.
    let private withRunInput (cwlType: string) (extraCwl: string) (runYml: string option) =
        let fixture = baseline ()
        let cwl = $"cwlVersion: v1.2\nclass: CommandLineTool\nbaseCommand: echo\ninputs:\n  input_file:\n    type: {cwlType}\n{extraCwl}outputs:\n  result:\n    type: File\n    outputBinding:\n      glob: result.csv\n"

        { fixture with
            ExtraFiles =
                fixture.ExtraFiles
                |> List.choose (fun (path, content) ->
                    match path, runYml with
                    | "runs/MyRun/run.cwl", _ -> Some(path, cwl)
                    | "runs/MyRun/run.yml", None -> None
                    | "runs/MyRun/run.yml", Some yml -> Some(path, yml)
                    | _ -> Some(path, content)) }

    /// An optional `File?` input with a valid `class: File` value.
    let optionalFileInput () = withRunInput "File?" "" (Some Cwl.runYml)

    /// An input with a CWL `default:` and no value in `run.yml`.
    let defaultedInput () = withRunInput "string" "    default: hello\n" None

    /// An optional input with neither a value in `run.yml` nor a default.
    let optionalUnassignedInput () = withRunInput "File?" "" None

    /// A `File` input whose CWL default is a Directory, with no value in `run.yml`.
    let mistypedDefault () =
        withRunInput
            "File"
            "    default:\n      class: Directory\n      path: ../../assays/MyAssay/dataset\n"
            None

    let noProcessTable () =
        let fixture = baseline ()
        fixture.Runs.Head.Tables <- ResizeArray []
        fixture

    /// A `Data` with no name at all: `CompositeCell.GetContent()` returns it as a single empty
    /// string, which is what `Readiness.cellIsBlank` treats as an empty cell.
    let blankInputCell () =
        let fixture = baseline ()
        let table = ArcTable.init "Process"
        table.AddColumn(CompositeHeader.Input IOType.Data, ResizeArray [ CompositeCell.createData (Data()) ])

        table.AddColumn(
            CompositeHeader.Output IOType.Data,
            ResizeArray [ CompositeCell.createData (Data(name = "runs/MyRun/result.csv")) ]
        )

        fixture.Runs.Head.Tables <- ResizeArray [ table ]
        fixture

    let noOutputColumn () =
        let fixture = baseline ()
        let table = ArcTable.init "Process"

        table.AddColumn(
            CompositeHeader.Input IOType.Data,
            ResizeArray [ CompositeCell.createData (Data(name = "assays/MyAssay/dataset/data.csv")) ]
        )

        fixture.Runs.Head.Tables <- ResizeArray [ table ]
        fixture

    let multipleFailuresWorkflow () =
        let fixture = baseline ()
        fixture.Workflows.Head.Title <- None
        fixture.Workflows.Head.Contacts <- ResizeArray []
        fixture

    let noLicense () = { baseline () with License = None }

    let emptyLicense () = { baseline () with License = Some "" }

    let lfsPointerDataFile () =
        let fixture = baseline ()

        { fixture with
            ExtraFiles =
                fixture.ExtraFiles
                |> List.map (fun (path, content) ->
                    if path = "assays/MyAssay/dataset/data.csv" then
                        path, lfsPointerText
                    else
                        path, content) }

    let mixedArc () =
        let secondWorkflow =
            ArcWorkflow(
                "SecondWorkflow",
                description = "A workflow that does things.",
                uri = "https://git.example.org/my/repo",
                version = "1.0.0",
                contacts = ResizeArray [ Person(firstName = "Ada", lastName = "Lovelace") ]
            )

        { Workflows = [ validWorkflow (); secondWorkflow ]
          Runs = [ validRun () ]
          License = Some licenseText
          ExtraFiles =
            baselineExtraFiles
            @ [ "workflows/SecondWorkflow/workflow.cwl", Cwl.workflowCwl
                "workflows/SecondWorkflow/tool.cwl", Cwl.toolCwl ] }
