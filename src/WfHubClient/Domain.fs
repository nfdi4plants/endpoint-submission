namespace WfHubClient

type ItemKind =
    | Workflow
    | Run

/// Two-phase pipeline: metadata checks first, then a pack attempt only for items that passed.
type ItemStatus =
    /// Failed the metadata readiness checks.
    | NotReady of failures: string list
    /// Checks passed; crate write / zip assembly failed, or the finished crate
    /// exceeds the upload size limit.
    | PackFailed of error: string
    | Uploadable of crate: byte[] * size: int64

type PackedItem =
    { Identifier: string
      Kind: ItemKind
      Title: string option
      Description: string option
      Status: ItemStatus }

type PackResult =
    { Items: PackedItem list
      /// None = no LICENSE file present (the crate would carry a placeholder license).
      License: string option
      /// The SPDX identifier the license text was recognized as, and the only form
      /// WorkflowHub accepts. None means the entry arrives without a license, leaving its
      /// owner to set one after upload.
      LicenseSpdxId: string option
      ArcLoadError: string option }

type Team = { Id: int; Title: string }

type PersonInfo =
    { Id: int
      Name: string
      TeamIds: int list }

/// Initial visibility, chosen per item by the submitting user.
type Visibility =
    /// Policy access "no_access" — only the submitter (and team managers) see it.
    | Private
    /// Policy access "download" — WorkflowHub's normal public entry.
    | Public

type UploadResult =
    { WorkflowId: int
      Url: string
      Version: int
      Applied: Visibility
      RawResponse: string }

type UploadError =
    | Rejected of status: int * detail: string
    | Transport of message: string
    /// The entry exists but setting its visibility failed; retry the visibility
    /// step rather than re-uploading — there is no delete to roll back with.
    | UploadedButPolicyUnset of workflowId: int * detail: string

/// Thrown by the WorkflowHub client so `Task`-returning methods stay C#-friendly while the
/// error stays typed; consumers catch this and switch on `Error`.
type WorkflowHubException(error: UploadError) =
    inherit System.Exception(
        match error with
        | Rejected(status, detail) -> $"WorkflowHub rejected the request with status {status}: {detail}"
        | Transport message -> $"Could not reach WorkflowHub: {message}"
        | UploadedButPolicyUnset(workflowId, detail) ->
            $"Workflow {workflowId} was uploaded, but setting its visibility failed: {detail}. Retry the visibility step; do not re-upload."
    )

    member _.Error = error
