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
      /// None = no LICENSE file present. Every item is then NotReady with the license reason.
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
      RawResponse: string
      /// The exact JSON body of the policy PATCH. Empty for `createVersion`, which sends none.
      PolicyRequest: string
      /// The raw body WorkflowHub answered the policy PATCH with. Empty for `createVersion`.
      PolicyRawResponse: string }

type UploadError =
    | Rejected of status: int * detail: string
    | Transport of message: string
    /// The entry exists but setting its visibility failed; retry the visibility
    /// step rather than re-uploading — there is no delete to roll back with.
    | UploadedButPolicyUnset of workflowId: int * detail: string
    /// The crate POST may have created an entry, but no usable answer arrived (a timeout, a dropped
    /// connection after sending, a 5xx status after sending, or a success status with an unreadable
    /// body). The entry may exist and may be public.
    | OutcomeUnknown of detail: string

/// Thrown by the WorkflowHub client so `Task`-returning methods stay C#-friendly. The error stays
/// typed, and consumers catch this and switch on `Error`. `RawBody` is the response body
/// the error came from, for the audit trail. It is empty when there is none.
///
/// `UploadedButPolicyUnset` also carries the failed visibility PATCH. `PolicyRequest` is the exact
/// body that was sent and `PolicyRawBody` is the raw body WorkflowHub answered with, so the audit
/// trail keeps both. Both are empty for every other error, and `PolicyRawBody` is also empty when
/// no response arrived.
type WorkflowHubException(error: UploadError, rawBody: string, policyRequest: string, policyRawBody: string) =
    inherit System.Exception(
        match error with
        | Rejected(status, detail) -> $"WorkflowHub rejected the request with status {status}: {detail}"
        | Transport message -> $"Could not reach WorkflowHub: {message}"
        | UploadedButPolicyUnset(workflowId, detail) ->
            $"Workflow {workflowId} was uploaded, but setting its visibility failed: {detail}. Retry the visibility step; do not re-upload."
        | OutcomeUnknown detail ->
            $"WorkflowHub did not confirm the upload: {detail} The entry may exist and may be public."
    )

    new(error: UploadError, rawBody: string) = WorkflowHubException(error, rawBody, "", "")
    new(error: UploadError) = WorkflowHubException(error, "", "", "")

    member _.Error = error
    member _.RawBody = rawBody
    member _.PolicyRequest = policyRequest
    member _.PolicyRawBody = policyRawBody
