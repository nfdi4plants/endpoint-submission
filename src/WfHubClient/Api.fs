namespace WfHubClient

module WorkflowHubApi =
    /// Authorization Code grant; PKCE supported.
    let AuthorizePath = "/oauth/authorize"
    let TokenPath = "/oauth/token"
    /// Tokens expire after roughly two hours.
    let Scope = "read write"
    /// SEEK policy access values used when setting visibility.
    let PolicyPrivate = "no_access"
    let PolicyPublic = "download"

    let CurrentPersonPath = "/people/current"
    let WorkflowsPath = "/workflows"
    let projectPath (projectId: int) = $"/projects/{projectId}"
    let workflowPath (workflowId: int) = $"/workflows/{workflowId}"
    let createVersionPath (workflowId: int) = $"/workflows/{workflowId}/create_version"

module WorkflowHubLimits =
    let MaxUploadBytes = 100L * 1024L * 1024L
