namespace DataHubGit

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

type CheckoutError =
    /// DataHUB refused the access token (HTTP 401 or 403, or git reported a failed login).
    | Unauthorized of detail: string
    /// The project, commit or LFS object does not exist, or the token cannot see it.
    | NotFound of detail: string
    | Network of detail: string
    /// The overall deadline passed. The partial checkout was deleted.
    | TimedOut
    /// git or git-lfs is not installed or does not start.
    | GitMissing of detail: string
    | GitFailed of detail: string
    | LfsFailed of detail: string

/// Thrown by `Checkout.checkout` so `Task`-returning methods stay C#-friendly while the error stays
/// typed. The message and the details come from redacted git output and never hold the token.
type CheckoutException(error: CheckoutError) =
    inherit
        Exception(
            match error with
            | Unauthorized detail -> $"DataHUB refused the access token: {detail}"
            | NotFound detail -> $"The DataHUB project or commit was not found: {detail}"
            | Network detail -> $"Could not reach DataHUB: {detail}"
            | TimedOut -> "The checkout did not finish within its time limit."
            | GitMissing detail -> $"git or git-lfs is not available: {detail}"
            | GitFailed detail -> $"git failed: {detail}"
            | LfsFailed detail -> $"git-lfs failed: {detail}"
        )

    member _.Error = error

/// A working tree of one DataHUB commit in a temporary folder. Dispose deletes the folder.
[<Sealed>]
type ArcCheckout internal (root: string, directory: string, commitSha: string, isShallow: bool) =
    /// The working tree root, which is the ARC directory.
    member _.Directory = directory
    /// The commit that was checked out, in lowercase hex.
    member _.CommitSha = commitSha
    member _.IsShallow = isShallow

    interface IDisposable with
        /// Deletes the working tree together with the isolated HOME and config files. Never throws.
        member _.Dispose() = GitProcess.deleteTree root

/// Clones one commit of a DataHUB project into a temporary folder with git. The token is a
/// method argument of one call: it is never stored in a field, a file, a URL or an exception.
module Checkout =

    let private rootPrefix = "datahubgit-checkout-"

    let private hexSha = Regex(@"^([0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", RegexOptions.Compiled)
    let private lowerHexSha = Regex(@"^([0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.Compiled)

    let private hostPattern =
        Regex(@"^[A-Za-z0-9][A-Za-z0-9.-]*(:[0-9]{1,5})?$", RegexOptions.Compiled)

    // The API call carries the token in a header, so it must not follow a redirect to another
    // host. It keeps no cookies, so a session from one user's call cannot authenticate the next
    // user's call. The pooled connections expire so a DNS change reaches a long-running service.
    let private httpClient =
        new HttpClient(
            new SocketsHttpHandler(
                UseCookies = false,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes 5.
            ),
            Timeout = Timeout.InfiniteTimeSpan
        )

    /// Splits a validated `host[:port]`, lowercases the name and defaults the port to 443.
    let private hostAndPort (host: string) : string * int =
        match host.LastIndexOf ':' with
        | -1 -> host.ToLowerInvariant(), 443
        | index -> host.Substring(0, index).ToLowerInvariant(), int (host.Substring(index + 1))

    let private isValidHost (host: string) =
        not (isNull host)
        && hostPattern.IsMatch host
        && (let _, port = hostAndPort host in port > 0 && port <= 65535)

    /// Maps redacted git stderr (LC_ALL=C) to an error. The checks run in order. The patterns are
    /// full phrases because a bare status number can turn up inside an LFS object id.
    let internal classify (stderr: string) : CheckoutError =
        let has (phrase: string) =
            stderr.Contains(phrase, StringComparison.OrdinalIgnoreCase)

        let any (phrases: string list) = phrases |> List.exists has
        let detail = GitProcess.tailLines 20 stderr

        if
            any
                [ "HTTP Basic: Access denied"
                  "Authentication failed"
                  "returned error: 401"
                  "returned error: 403"
                  "could not read Username"
                  // git-lfs prints no status code. A 401 ends in "Git credentials for <url> not
                  // found", and a 403 without a body message says "Authorization error". GitLab
                  // puts "Access forbidden" in the body of its 403.
                  "Git credentials for"
                  "Authorization error"
                  "Access forbidden" ]
        then
            Unauthorized detail
        elif
            any
                [ "Repository not found"
                  "returned error: 404"
                  "not our ref"
                  "couldn't find remote ref"
                  "unadvertised object" ]
        then
            NotFound detail
        elif
            any
                [ "Could not resolve host"
                  "Failed to connect"
                  "Connection timed out"
                  "Connection reset"
                  "Connection refused" ]
        then
            Network detail
        else
            GitFailed detail

    /// Root-anchors each pattern (`workflows/**` becomes `/workflows/**`).
    let internal lfsPatterns (lfsInclude: string list) : string list =
        lfsInclude
        |> List.map (fun pattern -> pattern.Trim().TrimStart '/')
        |> List.filter (fun pattern -> pattern <> "")
        |> List.map (fun pattern -> "/" + pattern)

    /// git-lfs keeps only the last of repeated `--include` flags, so all patterns share one.
    let internal lfsIncludeArgument (patterns: string list) : string =
        "--include=" + String.Join(",", patterns)

    /// The LFS endpoint git-lfs derives from a remote URL. It matches git-lfs: a file remote or a
    /// local path stays as it is, and an http(s) remote gets `/info/lfs` after `.git`.
    let internal lfsEndpoint (remoteUrl: string) : string =
        let isHttp =
            remoteUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || remoteUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)

        if not isHttp then
            remoteUrl
        else
            let trimmed = if remoteUrl.EndsWith "/" then remoteUrl.Substring(0, remoteUrl.Length - 1) else remoteUrl

            if trimmed.EndsWith(".git", StringComparison.Ordinal) then
                trimmed + "/info/lfs"
            else
                trimmed + ".git/info/lfs"

    let internal checkoutRemote
        (tempBase: string)
        (remoteUrl: string)
        (authHost: string option)
        (token: string option)
        (commitSha: string option)
        (lfsInclude: string list)
        (timeout: TimeSpan)
        (ct: CancellationToken)
        : Task<ArcCheckout> =
        task {
            let root = Path.Combine(tempBase, rootPrefix + Guid.NewGuid().ToString "N")
            let repo = Path.Combine(root, "repo")
            let home = Path.Combine(root, "home")
            let emptyConfig = Path.Combine(root, "gitconfig-empty")
            let succeeded = ref false

            // One deadline covers the whole checkout, whatever number of git calls it takes.
            use deadline = new CancellationTokenSource(timeout)
            use linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct)

            let secrets = token |> Option.map GitProcess.secretsOf |> Option.defaultValue []
            let patterns = lfsPatterns lfsInclude

            let auth =
                match authHost, token with
                | Some host, Some secret -> Some(GitProcess.authPair host secret)
                | _ -> None

            try
                try
                    Directory.CreateDirectory repo |> ignore
                    Directory.CreateDirectory home |> ignore
                    File.WriteAllText(emptyConfig, "")
                    // Only the two calls that talk to DataHUB get the token in their environment.
                    let plainEnvironment = GitProcess.buildEnvironment home emptyConfig None
                    let authEnvironment = GitProcess.buildEnvironment home emptyConfig auth
                    let git workDir args = GitProcess.run workDir plainEnvironment args linked.Token
                    let gitWithAuth workDir args = GitProcess.run workDir authEnvironment args linked.Token

                    // Details come from redacted stderr only, never from argv.
                    let stderrOf (result: RunResult) =
                        let text = GitProcess.redact secrets result.Stderr

                        if String.IsNullOrWhiteSpace text then
                            $"git exited with code {result.ExitCode}"
                        else
                            text

                    let fail (result: RunResult) : unit =
                        raise (CheckoutException(classify (stderrOf result)))

                    let lfsStep (result: RunResult) =
                        if
                            result.ExitCode <> 0
                            || result.Stderr.Contains("Error updating the Git index", StringComparison.Ordinal)
                        then
                            raise (CheckoutException(LfsFailed(GitProcess.tailLines 20 (stderrOf result))))

                    // A refused token during the LFS download is Unauthorized, as it is for the fetch.
                    let lfsFetchStep (result: RunResult) =
                        if result.ExitCode <> 0 then
                            match classify (stderrOf result) with
                            | Unauthorized _ as denied -> raise (CheckoutException denied)
                            | _ -> ()

                        lfsStep result

                    let! version = git root (GitProcess.commandArgs [ "--version" ])

                    if version.ExitCode <> 0 then
                        raise (CheckoutException(GitMissing(GitProcess.tailLines 20 (stderrOf version))))

                    if not patterns.IsEmpty then
                        let! lfsVersion = git root (GitProcess.commandArgs [ "lfs"; "version" ])

                        if lfsVersion.ExitCode <> 0 then
                            raise (CheckoutException(GitMissing(GitProcess.tailLines 20 (stderrOf lfsVersion))))

                    let! init = git repo (GitProcess.commandArgs [ "init"; "-q" ])

                    if init.ExitCode <> 0 then
                        fail init

                    // The ARC is untrusted, and a committed link could point at any file of the
                    // service, such as /proc/self/environ. With core.symlinks off, every later git
                    // and git-lfs call of this repository writes a link as a small regular file
                    // that holds the link target as text.
                    let! symlinks =
                        git repo (GitProcess.commandArgs [ "config"; "--local"; "core.symlinks"; "false" ])

                    if symlinks.ExitCode <> 0 then
                        fail symlinks

                    let! remote = git repo (GitProcess.commandArgs [ "remote"; "add"; "origin"; remoteUrl ])

                    if remote.ExitCode <> 0 then
                        fail remote

                    let! fetch = gitWithAuth repo (GitProcess.fetchArgs (defaultArg commitSha "HEAD"))

                    if fetch.ExitCode <> 0 then
                        fail fetch

                    let! checkedOut =
                        git
                            repo
                            (GitProcess.commandArgs
                                [ "-c"; "advice.detachedHead=false"; "checkout"; "-q"; "--detach"; "FETCH_HEAD" ])

                    if checkedOut.ExitCode <> 0 then
                        fail checkedOut

                    let! head = git repo (GitProcess.commandArgs [ "rev-parse"; "HEAD" ])

                    if head.ExitCode <> 0 then
                        fail head

                    let resolved = head.Stdout.Trim()

                    if not (lowerHexSha.IsMatch resolved) then
                        raise (CheckoutException(GitFailed "git did not report a valid commit id"))

                    match commitSha with
                    | Some expected when not (String.Equals(expected, resolved, StringComparison.OrdinalIgnoreCase)) ->
                        raise (CheckoutException(GitFailed "the checked out commit differs from the requested one"))
                    | _ -> ()

                    if not patterns.IsEmpty then
                        let overrides = GitProcess.lfsOverrides (lfsEndpoint remoteUrl)

                        let! install =
                            git repo (GitProcess.commandArgs [ "lfs"; "install"; "--local"; "--skip-smudge" ])

                        lfsStep install

                        let! lfsFetch =
                            gitWithAuth
                                repo
                                (GitProcess.commandArgs (
                                    [ "-c"; "lfs.fetchrecentalways=false" ]
                                    @ overrides
                                    @ [ "lfs"; "fetch"; lfsIncludeArgument patterns; "origin"; "HEAD" ]
                                ))

                        lfsFetchStep lfsFetch

                        let! lfsCheckout =
                            git repo (GitProcess.commandArgs (overrides @ [ "lfs"; "checkout"; "--" ] @ patterns))

                        lfsStep lfsCheckout

                    succeeded.Value <- true
                    return new ArcCheckout(root, repo, resolved, true)
                with :? OperationCanceledException when not ct.IsCancellationRequested ->
                    return raise (CheckoutException TimedOut)
            finally
                if not succeeded.Value then
                    GitProcess.deleteTree root
        }

    /// Accepts a clone URL only when it is https on exactly the configured host and port. The
    /// result is built from the parsed parts, so it has no userinfo and nothing after the path,
    /// and the remote written to `.git/config` is clean.
    let internal validateCloneUrl (host: string) (rawUrl: string) : string option =
        let expectedName, expectedPort = hostAndPort host

        match Uri.TryCreate(rawUrl, UriKind.Absolute) with
        | true, uri when
            uri.Scheme = Uri.UriSchemeHttps
            && String.Equals(uri.Host, expectedName, StringComparison.OrdinalIgnoreCase)
            && uri.Port = expectedPort
            ->
            let authority =
                if expectedPort = 443 then expectedName else $"{expectedName}:{expectedPort}"

            Some $"https://{authority}{uri.AbsolutePath}"
        | _ -> None

    /// Asks the DataHUB API for the project's clone URL and validates it.
    let private resolveRemoteUrl (host: string) (projectId: int) (token: string) (ct: CancellationToken) (cancelled: CancellationToken) =
        task {
            let secrets = GitProcess.secretsOf token

            try
                use request =
                    new HttpRequestMessage(HttpMethod.Get, $"https://{host}/api/v4/projects/{projectId}")

                request.Headers.Add("PRIVATE-TOKEN", token)
                use! response = httpClient.SendAsync(request, ct)
                let status = int response.StatusCode

                if status >= 300 && status < 400 then
                    raise (CheckoutException(Network $"The DataHUB API answered with a redirect (HTTP {status})."))
                elif response.StatusCode = HttpStatusCode.Unauthorized || response.StatusCode = HttpStatusCode.Forbidden then
                    raise (CheckoutException(Unauthorized $"the DataHUB API answered HTTP {status}"))
                elif response.StatusCode = HttpStatusCode.NotFound then
                    raise (CheckoutException(NotFound $"the DataHUB API answered HTTP {status}"))
                elif status >= 500 then
                    raise (CheckoutException(Network $"the DataHUB API answered HTTP {status}"))
                elif not response.IsSuccessStatusCode then
                    raise (CheckoutException(GitFailed $"the DataHUB API answered HTTP {status}"))

                let! body = response.Content.ReadAsStringAsync ct

                let rawUrl =
                    try
                        use document = JsonDocument.Parse body

                        match document.RootElement.TryGetProperty "http_url_to_repo" with
                        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
                        | _ -> null
                    with :? JsonException ->
                        null

                if isNull rawUrl then
                    raise (CheckoutException(GitFailed "the DataHUB API response has no clone URL"))

                match validateCloneUrl host rawUrl with
                | Some url -> return url
                | None -> return raise (CheckoutException(GitFailed "the DataHUB API returned a clone URL for another host"))
            with
            | :? HttpRequestException as e ->
                return raise (CheckoutException(Network(GitProcess.redact secrets e.Message)))
            | :? OperationCanceledException when not cancelled.IsCancellationRequested ->
                return raise (CheckoutException TimedOut)
        }

    /// Deletes the `datahubgit-checkout-*` folders of `tempBase` whose last write is older than
    /// `olderThan` and returns how many it removed. The sweep skips links. Each deletion is best
    /// effort.
    let internal sweepStaleRootsIn (tempBase: string) (olderThan: TimeSpan) : int =
        try
            let cutoff = DateTime.UtcNow - olderThan

            let isLink (path: string) =
                let info = DirectoryInfo path
                not (isNull info.LinkTarget) || info.Attributes.HasFlag FileAttributes.ReparsePoint

            Directory.EnumerateDirectories(tempBase, rootPrefix + "*")
            |> List.ofSeq
            |> List.filter (fun path ->
                try
                    not (isLink path) && Directory.GetLastWriteTimeUtc path < cutoff
                with _ ->
                    false)
            |> List.sumBy (fun path ->
                GitProcess.deleteTree path
                if Directory.Exists path then 0 else 1)
        with _ ->
            0

    /// Deletes the checkout folders in the temp directory that are older than `olderThan`, for
    /// example the ones a crashed process left behind, and returns how many it removed. Pass an
    /// age above the checkout timeout, because a folder that is still in use looks the same.
    let sweepStaleRoots (olderThan: TimeSpan) : int =
        sweepStaleRootsIn (Path.GetTempPath()) olderThan

    /// Checks out `commitSha` (null or empty: the default branch head) of the DataHUB project into a
    /// temporary folder. LFS files stay pointers except those under the `lfsInclude` patterns, for
    /// example `[ "workflows/**" ]` for the workflow folder. A null or empty `lfsInclude` hydrates
    /// nothing and skips git-lfs altogether. A pattern must not contain a comma, or the call throws
    /// `ArgumentException`. `timeout` covers the whole call. A negative `timeout` other than
    /// `Timeout.InfiniteTimeSpan` throws `ArgumentOutOfRangeException` before any work starts.
    /// Failures raise `CheckoutException`, and caller cancellation raises `OperationCanceledException`.
    let checkout
        (
            host: string,
            projectId: int,
            token: string,
            commitSha: string,
            lfsInclude: seq<string>,
            timeout: TimeSpan,
            cancellationToken: CancellationToken
        ) : Task<ArcCheckout> =
        if timeout < TimeSpan.Zero && timeout <> Timeout.InfiniteTimeSpan then
            raise (
                ArgumentOutOfRangeException(
                    "timeout",
                    "The timeout must not be negative. Use Timeout.InfiniteTimeSpan for no limit."
                )
            )

        let includes =
            match lfsInclude with
            | null -> []
            | patterns -> List.ofSeq patterns

        if includes |> List.exists (fun pattern -> not (isNull pattern) && pattern.Contains ',') then
            raise (ArgumentException("An LFS include pattern must not contain a comma.", "lfsInclude"))

        task {
            if not (isValidHost host) then
                raise (CheckoutException(GitFailed "invalid DataHUB host"))

            if String.IsNullOrEmpty token then
                raise (CheckoutException(Unauthorized "no access token was supplied"))

            // A token outside printable ASCII cannot be a header value, and the framework's
            // FormatException would quote it.
            if token |> Seq.exists (fun c -> c < '!' || c > '~') then
                raise (CheckoutException(Unauthorized "the access token has characters that are not valid in a header"))

            if projectId <= 0 then
                raise (CheckoutException(GitFailed "invalid project id"))

            if not (String.IsNullOrEmpty commitSha) && not (hexSha.IsMatch commitSha) then
                raise (CheckoutException(GitFailed "invalid commit id"))

            let clock = Stopwatch.StartNew()
            use deadline = new CancellationTokenSource(timeout)
            use linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken)
            let! remoteUrl = resolveRemoteUrl host projectId token linked.Token cancellationToken

            let remaining =
                if timeout = Timeout.InfiniteTimeSpan then
                    timeout
                else
                    let left = timeout - clock.Elapsed
                    if left < TimeSpan.Zero then TimeSpan.Zero else left

            return!
                checkoutRemote
                    (Path.GetTempPath())
                    remoteUrl
                    (Some host)
                    (Some token)
                    (if String.IsNullOrEmpty commitSha then None else Some commitSha)
                    includes
                    remaining
                    cancellationToken
        }
