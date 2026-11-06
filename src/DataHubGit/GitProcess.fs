namespace DataHubGit

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks

type internal RunResult =
    { ExitCode: int
      Stdout: string
      Stderr: string }

/// Process, environment, header and redaction helpers for `Checkout`. Nothing here keeps a
/// credential. A token arrives as an argument and goes only into the environment of one git
/// process (and its children) or into a redacted string. It never reaches argv.
module internal GitProcess =

    let private redacted = "[REDACTED]"

    let private userInfo = Regex(@"://[^/\s]+@", RegexOptions.Compiled)

    let private authorizationLine =
        Regex(@"(authorization\s*:)[^\r\n]*", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

    let private privateTokenLine =
        Regex(@"(private-token\s*:)[^\r\n]*", RegexOptions.IgnoreCase ||| RegexOptions.Compiled)

    /// Every form of the token that git, git-lfs or an HTTP library could echo back.
    let secretsOf (token: string) : string list =
        if String.IsNullOrEmpty token then
            []
        else
            [ token
              "oauth2:" + token
              Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth2:" + token))
              Uri.EscapeDataString token ]

    /// Replaces the known secrets (longest first, so a prefix never leaves a tail of a longer
    /// form behind), then masks URL userinfo and credential header lines whatever they contain.
    let redact (secrets: string list) (text: string) : string =
        let known =
            secrets
            |> List.filter (fun secret -> not (isNull secret) && secret.Length >= 4)
            |> List.distinct
            |> List.sortByDescending String.length

        let start = if isNull text then "" else text

        let replaced =
            known
            |> List.fold (fun (current: string) secret -> current.Replace(secret, redacted, StringComparison.Ordinal)) start

        let masked = userInfo.Replace(replaced, "://" + redacted + "@")
        let masked = authorizationLine.Replace(masked, "$1 " + redacted)
        privateTokenLine.Replace(masked, "$1 " + redacted)

    /// The last `count` non-empty lines, for exception details.
    let tailLines (count: int) (text: string) : string =
        text.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.rev
        |> Array.truncate count
        |> Array.rev
        |> String.concat "\n"

    /// The git config key and value that carry the token for one DataHUB host. The key is scoped
    /// to the host because git-lfs downloads from presigned S3 URLs on another host, and S3 rejects
    /// a request that carries an extra `Authorization` header.
    let authPair (host: string) (token: string) : string * string =
        let name, port =
            match host.LastIndexOf ':' with
            | -1 -> host, ""
            | index -> host.Substring(0, index), host.Substring(index + 1)

        let authority =
            if port = "" || port = "443" then
                name.ToLowerInvariant()
            else
                $"{name.ToLowerInvariant()}:{port}"

        $"http.https://{authority}/.extraHeader",
        "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("oauth2:" + token))

    /// Arguments of one git call. Every `-c` pair sits before the subcommand: after it, `clone -c`
    /// would write the pair into the new repository's config. The pairs here hold no secret,
    /// because git copies each `-c` pair into the environment of its children.
    let commandArgs (subcommand: string list) : string list =
        [ "-c"; "credential.helper="; yield! subcommand ]

    let fetchArgs (target: string) : string list =
        commandArgs [ "fetch"; "--depth"; "1"; "--no-tags"; "-q"; "origin"; target ]

    /// The `-c` pairs that pin git-lfs to `endpoint`. A committed `.lfsconfig` can set `lfs.url`
    /// and `lfs.fetchexclude`, and a `-c` value beats it. Without the pin, an ARC could send the
    /// LFS requests to any URL, such as an internal service.
    let lfsOverrides (endpoint: string) : string list =
        [ "-c"; "lfs.url=" + endpoint; "-c"; "lfs.fetchexclude=" ]

    /// The variables a git process needs to start, plus the proxy and CA variables it needs to
    /// reach DataHUB. Each one is copied only when it is set.
    let private inheritedVariables =
        [ "PATH"
          "SYSTEMROOT"
          "WINDIR"
          "COMSPEC"
          "PATHEXT"
          "TEMP"
          "TMP"
          "TMPDIR"
          "HTTPS_PROXY"
          "HTTP_PROXY"
          "NO_PROXY"
          "https_proxy"
          "http_proxy"
          "no_proxy"
          "SSL_CERT_FILE"
          "SSL_CERT_DIR" ]

    /// The complete child environment. It starts empty and takes over only the allowlist above, so
    /// a host variable such as GIT_ASKPASS or GIT_TRACE never reaches git. `auth`, when present,
    /// becomes the `GIT_CONFIG_*` variables of one injected setting (git 2.31 or later). That
    /// keeps the token out of argv and out of `.git/config`. Every git child of the process
    /// (git-remote-https and git-lfs) inherits it.
    let buildEnvironment (home: string) (emptyConfig: string) (auth: (string * string) option) : (string * string) list =
        [ for name in inheritedVariables do
              match Environment.GetEnvironmentVariable name with
              | null
              | "" -> ()
              | value -> yield name, value

          yield "HOME", home
          yield "USERPROFILE", home
          yield "XDG_CONFIG_HOME", Path.Combine(home, ".config")
          yield "GIT_CONFIG_NOSYSTEM", "1"
          yield "GIT_CONFIG_GLOBAL", emptyConfig
          yield "GIT_TERMINAL_PROMPT", "0"
          yield "GCM_INTERACTIVE", "never"
          yield "LC_ALL", "C"
          yield "GIT_LFS_SKIP_SMUDGE", "1"

          match auth with
          | Some(key, value) ->
              yield "GIT_CONFIG_COUNT", "1"
              yield "GIT_CONFIG_KEY_0", key
              yield "GIT_CONFIG_VALUE_0", value
          | None -> () ]

    /// Runs git with `ArgumentList` and a cleared environment, and closes stdin. Cancelling `token`
    /// kills the whole process tree (git starts git-remote-https and git-lfs as children) and
    /// raises `OperationCanceledException`. A git that cannot start yields exit code -1.
    let run
        (workDir: string)
        (environment: (string * string) list)
        (args: string list)
        (token: CancellationToken)
        : Task<RunResult> =
        task {
            token.ThrowIfCancellationRequested()
            use proc = new Process()
            let info = proc.StartInfo
            info.FileName <- "git"
            info.WorkingDirectory <- workDir
            info.UseShellExecute <- false
            info.CreateNoWindow <- true
            info.RedirectStandardInput <- true
            info.RedirectStandardOutput <- true
            info.RedirectStandardError <- true
            info.StandardOutputEncoding <- Encoding.UTF8
            info.StandardErrorEncoding <- Encoding.UTF8
            info.Environment.Clear()

            for name, value in environment do
                info.Environment[name] <- value

            for arg in args do
                info.ArgumentList.Add arg

            let started =
                try
                    proc.Start() |> ignore
                    None
                with :? Win32Exception as e ->
                    Some e.Message

            match started with
            | Some message ->
                return
                    { ExitCode = -1
                      Stdout = ""
                      Stderr = "could not start git: " + message }
            | None ->
                proc.StandardInput.Close()

                use _killOnCancel =
                    token.Register(fun () ->
                        try
                            proc.Kill(true)
                        with _ ->
                            ())

                let stdoutTask = proc.StandardOutput.ReadToEndAsync()
                let stderrTask = proc.StandardError.ReadToEndAsync()
                do! proc.WaitForExitAsync()
                let! stdout = stdoutTask
                let! stderr = stderrTask
                token.ThrowIfCancellationRequested()

                return
                    { ExitCode = proc.ExitCode
                      Stdout = stdout
                      Stderr = stderr }
        }

    /// Deletes a folder tree on a best-effort basis and never throws. Git writes object files as
    /// read-only, which blocks deletion on Windows, so the attribute is cleared first. The walk
    /// skips symbolic links and reparse points.
    let deleteTree (path: string) : unit =
        let isLink (entry: string) =
            let info: FileSystemInfo =
                if Directory.Exists entry then DirectoryInfo entry else FileInfo entry

            not (isNull info.LinkTarget) || info.Attributes.HasFlag FileAttributes.ReparsePoint

        let rec clearReadOnly (directory: string) =
            for entry in Directory.EnumerateFileSystemEntries directory do
                if not (isLink entry) then
                    if Directory.Exists entry then
                        clearReadOnly entry
                    else
                        let attributes = File.GetAttributes entry

                        if attributes.HasFlag FileAttributes.ReadOnly then
                            File.SetAttributes(entry, attributes &&& ~~~FileAttributes.ReadOnly)

        let rec attempt (remaining: int) =
            try
                if Directory.Exists path then
                    clearReadOnly path
                    Directory.Delete(path, true)
            with _ when remaining > 1 ->
                Thread.Sleep 100
                attempt (remaining - 1)

        try
            attempt 3
        with _ ->
            ()
