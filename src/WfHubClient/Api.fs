namespace WfHubClient

module WorkflowHubApi =
    /// The OAuth endpoints of WorkflowHub (SEEK runs Doorkeeper).
    let AuthorizePath = "/oauth/authorize"
    let TokenPath = "/oauth/token"
    /// Token revocation (RFC 7009).
    let RevokePath = "/oauth/revoke"
    /// WorkflowHub grants `read` by default. Uploading needs `write` as well.
    let Scope = "read write"

    /// The address that sends the user to WorkflowHub to log in and confirm the upload
    /// (authorization code grant with PKCE S256). Every value is percent-encoded.
    let authorizeUrl (baseUrl: string, clientId: string, redirectUri: string, state: string, codeChallenge: string) : string =
        let encode (value: string) = System.Uri.EscapeDataString value

        $"{baseUrl.TrimEnd '/'}{AuthorizePath}?response_type=code&client_id={encode clientId}"
        + $"&redirect_uri={encode redirectUri}&scope={encode Scope}&state={encode state}"
        + $"&code_challenge={encode codeChallenge}&code_challenge_method=S256"

    /// SEEK policy access values used when setting visibility.
    let PolicyPrivate = "no_access"
    let PolicyPublic = "download"

    let CurrentPersonPath = "/people/current"
    let WorkflowsPath = "/workflows"
    let projectPath (projectId: int) = $"/projects/{projectId}"
    let workflowPath (workflowId: int) = $"/workflows/{workflowId}"
    let createVersionPath (workflowId: int) = $"/workflows/{workflowId}/create_version"

    /// The multipart field that carries the crate zip.
    let CrateFieldName = "ro_crate"
    /// The file name of the crate part.
    let CrateFileName = "ro-crate.zip"
    /// The content type of the crate part.
    let CrateContentType = "application/zip"
    /// The multipart field that carries the id of the team that owns the new entry, once per team.
    let TeamFieldName = "workflow[project_ids][]"

module WorkflowHubLimits =
    let MaxUploadBytes = 100L * 1024L * 1024L
