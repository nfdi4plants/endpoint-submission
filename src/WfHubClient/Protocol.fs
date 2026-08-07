namespace WfHubClient

open Thoth.Json.Core

/// Parsing of WorkflowHub's (SEEK's) JSONAPI response bodies and shaping of request bodies.
/// Pure string work only; the HTTP calls live in `Client`.
module Protocol =

    /// What the upload endpoints answer with, before the client resolves the link.
    type UploadResponse =
        { Id: int
          SelfLink: string
          Version: int
          /// `attributes.policy.access` when the response exposes it (it does for the owner).
          PolicyAccess: string option }

    /// JSONAPI ids are strings ("42"); tolerate plain numbers too.
    let private stringlyInt: Decoder<int> =
        Decode.oneOf [
            Decode.int
            Decode.string
            |> Decode.andThen (fun s ->
                match System.Int32.TryParse s with
                | true, value -> Decode.succeed value
                | _ -> Decode.fail $"'{s}' is not an integer id")
        ]

    let private personDecoder: Decoder<PersonInfo> =
        Decode.field
            "data"
            (Decode.map3
                (fun id name teamIds ->
                    { Id = id
                      Name = name
                      TeamIds = teamIds })
                (Decode.field "id" stringlyInt)
                (Decode.at [ "attributes"; "title" ] Decode.string)
                (Decode.oneOf [
                    Decode.at
                        [ "relationships"; "projects"; "data" ]
                        (Decode.list (Decode.field "id" stringlyInt))
                    Decode.succeed []
                ]))

    let private teamDecoder: Decoder<Team> =
        Decode.field
            "data"
            (Decode.map2
                (fun id title -> { Id = id; Title = title })
                (Decode.field "id" stringlyInt)
                (Decode.at [ "attributes"; "title" ] Decode.string))

    let private uploadResponseDecoder: Decoder<UploadResponse> =
        Decode.field
            "data"
            (Decode.map4
                (fun id link version access ->
                    { Id = id
                      SelfLink = link
                      Version = version
                      PolicyAccess = access })
                (Decode.field "id" stringlyInt)
                (Decode.at [ "links"; "self" ] Decode.string)
                (Decode.oneOf [
                    Decode.at [ "attributes"; "version" ] stringlyInt
                    Decode.at [ "attributes"; "latest_version" ] stringlyInt
                ])
                (Decode.oneOf [
                    Decode.at [ "attributes"; "policy"; "access" ] Decode.string |> Decode.map Some
                    Decode.succeed None
                ]))

    /// The human-readable part of a JSONAPI error body, when there is one.
    let private errorDetailDecoder: Decoder<string list> =
        Decode.field
            "errors"
            (Decode.list (
                Decode.oneOf [
                    Decode.field "detail" Decode.string
                    Decode.field "title" Decode.string
                ]
            ))

    let parsePerson (json: string) : Result<PersonInfo, string> =
        Thoth.Json.Newtonsoft.Decode.fromString personDecoder json

    let parseTeam (json: string) : Result<Team, string> =
        Thoth.Json.Newtonsoft.Decode.fromString teamDecoder json

    let parseUploadResponse (json: string) : Result<UploadResponse, string> =
        Thoth.Json.Newtonsoft.Decode.fromString uploadResponseDecoder json

    /// The detail for a `Rejected` error: the JSONAPI error messages when the body carries
    /// them, the raw body otherwise.
    let rejectionDetail (body: string) : string =
        match Thoth.Json.Newtonsoft.Decode.fromString errorDetailDecoder body with
        | Ok(_ :: _ as details) -> String.concat "; " details
        | _ -> body

    /// The JSONAPI body for the policy PATCH that applies an entry's visibility.
    let policyBody (workflowId: int) (visibility: Visibility) : string =
        let access =
            match visibility with
            | Private -> WorkflowHubApi.PolicyPrivate
            | Public -> WorkflowHubApi.PolicyPublic

        Encode.object [
            "data",
            Encode.object [
                "id", Encode.string (string workflowId)
                "type", Encode.string "workflows"
                "attributes",
                Encode.object [ "policy", Encode.object [ "access", Encode.string access ] ]
            ]
        ]
        |> Thoth.Json.Newtonsoft.Encode.toString 0

    /// JSONAPI `links.self` is instance-relative; absolute links pass through unchanged.
    let resolveLink (baseUrl: string) (link: string) : string =
        if link.StartsWith "http://" || link.StartsWith "https://" then
            link
        else
            baseUrl.TrimEnd '/' + (if link.StartsWith "/" then link else "/" + link)
