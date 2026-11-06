module DataHubGit.Tests.CheckoutTests

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Expecto
open DataHubGit

let private wait (task: Task<'a>) = task.GetAwaiter().GetResult()

let private testToken = "glpat-TESTTOKEN-1234567890"

let private base64Of (text: string) =
    Convert.ToBase64String(Encoding.UTF8.GetBytes text)

let private basicPayload = base64Of ("oauth2:" + testToken)

/// A throwaway folder holding the fixture repository, an isolated HOME for the fixture's own git
/// calls and the folder that checkouts are created in.
type private Scratch(root: string) =
    let home = Path.Combine(root, "home")
    let emptyConfig = Path.Combine(root, "gitconfig-empty")

    do
        Directory.CreateDirectory home |> ignore
        Directory.CreateDirectory(Path.Combine(root, "out")) |> ignore
        File.WriteAllText(emptyConfig, "")

    member _.Out = Path.Combine(root, "out")
    member _.Repo = Path.Combine(root, "repo")
    member _.Home = home
    member _.EmptyConfig = emptyConfig

    interface IDisposable with
        member _.Dispose() = GitProcess.deleteTree root

let private newScratch () =
    new Scratch(Path.Combine(Path.GetTempPath(), "datahubgit-tests-" + Guid.NewGuid().ToString "N"))

/// Runs git for fixture setup. The identity is set per call and the global and system config
/// are ignored, so no developer setting leaks into the fixture.
let private git (scratch: Scratch) (dir: string) (args: string list) : string =
    let result =
        GitProcess.run
            dir
            (GitProcess.buildEnvironment scratch.Home scratch.EmptyConfig None)
            (GitProcess.commandArgs (
                [ "-c"; "user.name=Fixture"; "-c"; "user.email=fixture@example.org"; "-c"; "commit.gpgsign=false" ]
                @ args
            ))
            CancellationToken.None
        |> wait

    if result.ExitCode <> 0 then
        failtestf "git %s failed: %s" (String.concat " " args) result.Stderr

    result.Stdout.Trim()

let private writeFile (root: string) (relative: string) (content: string) =
    let path = Path.Combine(root, relative)
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, content)

let private fileUrl (path: string) = Uri(path).AbsoluteUri

let private timeout = TimeSpan.FromMinutes 2.

let private checkoutOf
    (scratch: Scratch)
    (authHost: string option)
    (token: string option)
    (commitSha: string option)
    (lfsInclude: string list)
    =
    Checkout.checkoutRemote
        scratch.Out
        (fileUrl scratch.Repo)
        authHost
        token
        commitSha
        lfsInclude
        timeout
        CancellationToken.None
    |> wait

/// Two commits. The second changes only README.md.
let private twoCommitRepo (scratch: Scratch) =
    Directory.CreateDirectory scratch.Repo |> ignore
    git scratch scratch.Repo [ "init"; "-q" ] |> ignore
    // A shallow fetch by commit id needs the server side to allow unadvertised ids.
    git scratch scratch.Repo [ "config"; "uploadpack.allowReachableSHA1InWant"; "true" ] |> ignore
    git scratch scratch.Repo [ "config"; "uploadpack.allowAnySHA1InWant"; "true" ] |> ignore
    writeFile scratch.Repo "workflows/w/workflow.cwl" "cwlVersion: v1.2\n"
    writeFile scratch.Repo "runs/r/run.yml" "input: 1\n"
    writeFile scratch.Repo "README.md" "first\n"
    git scratch scratch.Repo [ "add"; "-A" ] |> ignore
    git scratch scratch.Repo [ "commit"; "-q"; "-m"; "first" ] |> ignore
    let first = git scratch scratch.Repo [ "rev-parse"; "HEAD" ]
    writeFile scratch.Repo "README.md" "second\n"
    git scratch scratch.Repo [ "commit"; "-q"; "-a"; "-m"; "second" ] |> ignore
    first, git scratch scratch.Repo [ "rev-parse"; "HEAD" ]

/// One commit that holds a symbolic link entry (index mode 120000) named `name`. The entry goes in
/// through the index, so the fixture needs no symlink support from the operating system.
let private symlinkRepo (scratch: Scratch) (name: string) (target: string) =
    Directory.CreateDirectory scratch.Repo |> ignore
    git scratch scratch.Repo [ "init"; "-q" ] |> ignore
    writeFile scratch.Repo "README.md" "readme\n"
    // The git runner closes stdin, so the blob comes from a file outside the repository.
    let targetFile = Path.Combine(scratch.Home, "link-target")
    File.WriteAllText(targetFile, target)
    let blob = git scratch scratch.Repo [ "hash-object"; "-w"; "--no-filters"; targetFile ]
    git scratch scratch.Repo [ "add"; "README.md" ] |> ignore
    git scratch scratch.Repo [ "update-index"; "--add"; "--cacheinfo"; $"120000,{blob},{name}" ] |> ignore
    git scratch scratch.Repo [ "commit"; "-q"; "-m"; "link" ] |> ignore

let private lfsRepo (scratch: Scratch) (extraFiles: (string * string) list) =
    Directory.CreateDirectory scratch.Repo |> ignore
    git scratch scratch.Repo [ "init"; "-q" ] |> ignore
    git scratch scratch.Repo [ "lfs"; "install"; "--local" ] |> ignore
    writeFile scratch.Repo ".gitattributes" "*.bin filter=lfs diff=lfs merge=lfs -text\n"
    writeFile scratch.Repo "workflows/w/model.bin" "model content\n"
    writeFile scratch.Repo "runs/r/data.bin" "data content\n"

    for relative, content in extraFiles do
        writeFile scratch.Repo relative content

    git scratch scratch.Repo [ "add"; "-A" ] |> ignore
    git scratch scratch.Repo [ "commit"; "-q"; "-m"; "lfs" ] |> ignore

let private allText (root: string) =
    Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    |> Seq.map (fun file -> file, Encoding.Latin1.GetString(File.ReadAllBytes file))
    |> List.ofSeq

let private failureOf (run: unit -> 'a) : CheckoutException =
    try
        run () |> ignore
        failtest "expected a CheckoutException"
    with :? CheckoutException as e ->
        e

let private redactCases =
    let spaced = "p@ss/w:rd+x"

    [ "plain token", [ testToken ], $"fatal: bad value {testToken} here", "fatal: bad value [REDACTED] here"
      "oauth2:token", GitProcess.secretsOf testToken, $"user oauth2:{testToken} failed", "user [REDACTED] failed"
      "base64 of oauth2:token",
      GitProcess.secretsOf testToken,
      $"got Basic {basicPayload} back",
      "got Basic [REDACTED] back"
      "percent-encoded token",
      GitProcess.secretsOf spaced,
      $"see x={Uri.EscapeDataString spaced} end",
      "see x=[REDACTED] end"
      "URL userinfo without a known secret",
      [],
      "unable to access 'https://user:other@gitdev.example.org/x.git/'",
      "unable to access 'https://[REDACTED]@gitdev.example.org/x.git/'"
      "Authorization line",
      [],
      "> Authorization: Basic abc123\nnext",
      "> Authorization: [REDACTED]\nnext"
      "PRIVATE-TOKEN line", [], "PRIVATE-TOKEN:   zzzz9999\nnext", "PRIVATE-TOKEN: [REDACTED]\nnext"
      "short secrets are skipped", [ "abc" ], "abc def", "abc def" ]

let private classifyCases =
    [ "access denied", "remote: HTTP Basic: Access denied", "Unauthorized"
      "failed login", "fatal: Authentication failed for 'https://h/x.git/'", "Unauthorized"
      "403", "fatal: unable to access: The requested URL returned error: 403", "Unauthorized"
      "no username", "fatal: could not read Username for 'https://h': terminal prompts disabled", "Unauthorized"
      "missing repository", "remote: Repository not found.", "NotFound"
      "not our ref", "fatal: remote error: upload-pack: not our ref abc", "NotFound"
      "missing ref", "fatal: couldn't find remote ref refs/heads/x", "NotFound"
      "dns", "fatal: unable to access 'https://h/': Could not resolve host: h", "Network"
      "refused", "fatal: unable to access 'https://h/': Failed to connect to h port 443", "Network"
      "git-lfs 401 without credentials", "batch response: Git credentials for https://h/x.git not found.", "Unauthorized"
      "git-lfs 403 without a body message",
      "batch response: Authorization error: https://h/x.git/info/lfs/objects/batch\nCheck that you have proper access to the repository",
      "Unauthorized"
      "git-lfs 403 with GitLab's message", "batch response: Access forbidden. Check your access level.", "Unauthorized"
      "git-lfs server error", "batch response: Fatal error: nope", "GitFailed"
      "other failure", "fatal: something else broke", "GitFailed"
      "a bare 401 inside an object id is not an auth failure", "error: object 6401abc is corrupt", "GitFailed" ]

let private lfsEndpointCases =
    [ "https remote with .git", "https://h.example/g/p.git", "https://h.example/g/p.git/info/lfs"
      "https remote without .git", "https://h.example/g/p", "https://h.example/g/p.git/info/lfs"
      "https remote with a trailing slash", "https://h.example/g/p/", "https://h.example/g/p.git/info/lfs"
      "https remote with a port", "https://h.example:8443/g/p.git", "https://h.example:8443/g/p.git/info/lfs"
      "file remote", "file:///C:/x/y", "file:///C:/x/y"
      "file remote ending in .git", "file:///C:/x/y.git", "file:///C:/x/y.git"
      "local path", "/srv/repos/p", "/srv/repos/p" ]

let private cloneUrlCases =
    let host = "gitdev.example.org"
    let clean = "https://gitdev.example.org/g/p.git"

    [ "plain https url", host, clean, Some clean
      "host case is normalized", "GitDev.Example.org", "https://GITDEV.example.org/g/p.git", Some clean
      "explicit port 443", "gitdev.example.org:443", clean, Some clean
      "userinfo is dropped", host, "https://oauth2:secret@gitdev.example.org/g/p.git", Some clean
      "query and fragment are dropped", host, clean + "?x=1#top", Some clean
      "configured port is kept", "h.example:8443", "https://h.example:8443/g/p.git", Some "https://h.example:8443/g/p.git"
      "http scheme", host, "http://gitdev.example.org/g/p.git", None
      "ssh scheme", host, "ssh://git@gitdev.example.org/g/p.git", None
      "other host", host, "https://evil.example.net/g/p.git", None
      "host with the right prefix", host, "https://gitdev.example.org.evil.net/g/p.git", None
      "other port", host, "https://gitdev.example.org:8443/g/p.git", None
      "default port where another is configured", "h.example:8443", "https://h.example/g/p.git", None
      "relative url", host, "/g/p.git", None
      "empty url", host, "", None ]

let private inputCases =
    let host = "gitdev.example.org"
    let sha = String('a', 40)

    [ "empty host", "", 1, "tok", sha, GitFailed "invalid DataHUB host"
      "null host", null, 1, "tok", sha, GitFailed "invalid DataHUB host"
      "host with a path", "gitdev.example.org/x", 1, "tok", sha, GitFailed "invalid DataHUB host"
      "host with a port out of range", "gitdev.example.org:99999", 1, "tok", sha, GitFailed "invalid DataHUB host"
      "empty token", host, 1, "", sha, Unauthorized "no access token was supplied"
      "null token", host, 1, null, sha, Unauthorized "no access token was supplied"
      "token with a space",
      host,
      1,
      "glpat token",
      sha,
      Unauthorized "the access token has characters that are not valid in a header"
      "project id zero", host, 0, "tok", sha, GitFailed "invalid project id"
      "negative project id", host, -5, "tok", sha, GitFailed "invalid project id"
      "commit id with non-hex characters", host, 1, "tok", String('g', 40), GitFailed "invalid commit id"
      "commit id of the wrong length", host, 1, "tok", String('a', 39), GitFailed "invalid commit id" ]

[<Tests>]
let tests =
    testList "checkout" [
        testCase "a shallow checkout at a commit id has exactly that commit's files" <| fun () ->
            use scratch = newScratch ()
            let first, _ = twoCommitRepo scratch
            use checkout = checkoutOf scratch None None (Some first) []

            Expect.equal checkout.CommitSha first "the reported commit"
            Expect.isTrue checkout.IsShallow "IsShallow"
            Expect.equal (File.ReadAllText(Path.Combine(checkout.Directory, "README.md"))) "first\n" "README of the first commit"
            Expect.isTrue (File.Exists(Path.Combine(checkout.Directory, "workflows", "w", "workflow.cwl"))) "workflow file"
            Expect.isTrue (File.Exists(Path.Combine(checkout.Directory, "runs", "r", "run.yml"))) "run file"
            Expect.equal (git scratch checkout.Directory [ "rev-parse"; "--is-shallow-repository" ]) "true" "git agrees"

        testCase "without a commit id the default branch head is checked out" <| fun () ->
            use scratch = newScratch ()
            let _, second = twoCommitRepo scratch
            use checkout = checkoutOf scratch None None None []

            Expect.equal checkout.CommitSha second "the latest commit"
            Expect.equal (File.ReadAllText(Path.Combine(checkout.Directory, "README.md"))) "second\n" "README of the second commit"

        testCase "a committed symbolic link is checked out as a plain file that holds the target text" <| fun () ->
            use scratch = newScratch ()
            let target = "/proc/self/environ"
            symlinkRepo scratch "LICENSE" target
            use checkout = checkoutOf scratch None None None []
            let license = FileInfo(Path.Combine(checkout.Directory, "LICENSE"))

            let entry = git scratch checkout.Directory [ "ls-files"; "-s"; "LICENSE" ]
            Expect.stringStarts entry "120000 " "the index still records the entry as a symbolic link"
            Expect.isTrue license.Exists "LICENSE exists in the working tree"
            Expect.isNull license.LinkTarget "LICENSE is not a symbolic link"
            Expect.isFalse (license.Attributes.HasFlag FileAttributes.ReparsePoint) "LICENSE is not a reparse point"
            Expect.equal (File.ReadAllText license.FullName) target "the file holds the link target as text"

            Expect.equal
                (git scratch checkout.Directory [ "config"; "--get"; "core.symlinks" ])
                "false"
                "the checkout repository turns symbolic links off"

        testCase "dispose deletes the whole temporary root" <| fun () ->
            use scratch = newScratch ()
            twoCommitRepo scratch |> ignore
            let checkout = checkoutOf scratch None None None []
            let root = Path.GetDirectoryName checkout.Directory
            Expect.isTrue (Directory.Exists checkout.Directory) "the working tree exists before dispose"
            (checkout :> IDisposable).Dispose()
            Expect.isFalse (Directory.Exists checkout.Directory) "the working tree is gone"
            Expect.isFalse (Directory.Exists root) "the root with HOME and config files is gone"

        testCase "an unknown commit fails without leaving a folder or the token" <| fun () ->
            use scratch = newScratch ()
            twoCommitRepo scratch |> ignore
            let unknown = String('0', 40)

            let error =
                failureOf (fun () -> checkoutOf scratch (Some "gitlab.example.org") (Some testToken) (Some unknown) [])

            match error.Error with
            | NotFound _
            | GitFailed _ -> ()
            | other -> failtestf "unexpected error %A" other

            Expect.isEmpty (Directory.GetFileSystemEntries scratch.Out) "the temporary root is deleted"
            let shown = error.Message + sprintf "%A" error.Error

            for secret in [ testToken; basicPayload; "oauth2:" ] do
                Expect.isFalse (shown.Contains secret) $"the error must not contain {secret}"

        testCase "after a credentialed checkout no file in the temporary root holds the token" <| fun () ->
            use scratch = newScratch ()
            twoCommitRepo scratch |> ignore
            use checkout = checkoutOf scratch (Some "gitlab.example.org") (Some testToken) None []
            let root = Path.GetDirectoryName checkout.Directory
            let files = allText root
            Expect.isNonEmpty files "the root has files"

            for file, text in files do
                for needle in [ testToken; basicPayload; "extraHeader" ] do
                    Expect.isFalse (text.Contains needle) $"{file} contains {needle}"

            let gitConfig = File.ReadAllText(Path.Combine(checkout.Directory, ".git", "config"))
            Expect.stringContains gitConfig "[remote \"origin\"]" "the repository config exists"

            for needle in [ testToken; basicPayload; "extraHeader" ] do
                Expect.isFalse (gitConfig.Contains needle) $".git/config contains {needle}"

        testCase "the auth header key is scoped to the lowercase host and drops port 443" <| fun () ->
            let key host = fst (GitProcess.authPair host "tok")
            Expect.equal (key "GitDev.NFDI4Plants.org") "http.https://gitdev.nfdi4plants.org/.extraHeader" "no port"
            Expect.equal (key "gitdev.nfdi4plants.org:443") "http.https://gitdev.nfdi4plants.org/.extraHeader" "443 dropped"
            Expect.equal (key "Host.example:8443") "http.https://host.example:8443/.extraHeader" "other ports stay"

            Expect.equal
                (snd (GitProcess.authPair "h" "tok"))
                ("Authorization: Basic " + base64Of "oauth2:tok")
                "Basic payload"

        testCase "the fetch command line holds no auth pair and starts with an empty credential helper" <| fun () ->
            let args = GitProcess.fetchArgs "HEAD"
            Expect.equal (args |> List.take 2) [ "-c"; "credential.helper=" ] "an empty credential helper comes first"
            Expect.isFalse (args |> List.exists (fun a -> a.Contains "extraHeader")) "no header setting in argv"
            Expect.isFalse (args |> List.exists (fun a -> a.Contains testToken)) "the raw token is not in argv"

        testCase "the auth pair reaches git through GIT_CONFIG variables and only when given" <| fun () ->
            let key, value = GitProcess.authPair "h.example" testToken
            let find environment name = environment |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd
            let withAuth = GitProcess.buildEnvironment "home-dir" "empty-config" (Some(key, value))
            Expect.equal (find withAuth "GIT_CONFIG_COUNT") (Some "1") "one injected setting"
            Expect.equal (find withAuth "GIT_CONFIG_KEY_0") (Some "http.https://h.example/.extraHeader") "the host-scoped key"
            Expect.equal (find withAuth "GIT_CONFIG_VALUE_0") (Some("Authorization: Basic " + base64Of ("oauth2:" + testToken))) "the header"
            let without = GitProcess.buildEnvironment "home-dir" "empty-config" None
            Expect.isNone (find without "GIT_CONFIG_COUNT") "no injected setting without a token"

        testCase "the child environment is an allowlist plus isolated settings" <| fun () ->
            let traced = Environment.GetEnvironmentVariable "GIT_TRACE"
            let askpass = Environment.GetEnvironmentVariable "GIT_ASKPASS"
            Environment.SetEnvironmentVariable("GIT_TRACE", "1")
            Environment.SetEnvironmentVariable("GIT_ASKPASS", "x")

            try
                let environment = GitProcess.buildEnvironment "home-dir" "empty-config" None
                let find name = environment |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd
                Expect.equal (find "GIT_CONFIG_NOSYSTEM") (Some "1") "no system config"
                Expect.equal (find "GIT_TERMINAL_PROMPT") (Some "0") "no prompts"
                Expect.equal (find "GIT_CONFIG_GLOBAL") (Some "empty-config") "empty global config"
                Expect.equal (find "HOME") (Some "home-dir") "isolated home"

                let gitNames =
                    environment |> List.map fst |> List.filter (fun n -> n.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase))

                Expect.sequenceEqual
                    (List.sort gitNames)
                    [ "GIT_CONFIG_GLOBAL"; "GIT_CONFIG_NOSYSTEM"; "GIT_LFS_SKIP_SMUDGE"; "GIT_TERMINAL_PROMPT" ]
                    "only the library's own GIT_ variables"
            finally
                Environment.SetEnvironmentVariable("GIT_TRACE", traced)
                Environment.SetEnvironmentVariable("GIT_ASKPASS", askpass)

        testCase "proxy and CA variables pass through when set and are skipped when unset" <| fun () ->
            let names = [ "HTTPS_PROXY"; "SSL_CERT_FILE"; "SSL_CERT_DIR" ]
            let saved = names |> List.map (fun name -> name, Environment.GetEnvironmentVariable name)
            Environment.SetEnvironmentVariable("HTTPS_PROXY", "http://proxy.example:3128")
            Environment.SetEnvironmentVariable("SSL_CERT_FILE", "/etc/ssl/ca.pem")
            Environment.SetEnvironmentVariable("SSL_CERT_DIR", null)

            try
                let environment = GitProcess.buildEnvironment "home-dir" "empty-config" None
                let find name = environment |> List.tryFind (fun (n, _) -> n = name) |> Option.map snd
                Expect.equal (find "HTTPS_PROXY") (Some "http://proxy.example:3128") "proxy is passed"
                Expect.equal (find "SSL_CERT_FILE") (Some "/etc/ssl/ca.pem") "CA file is passed"
                Expect.isNone (find "SSL_CERT_DIR") "an unset variable is not added"
            finally
                for name, value in saved do
                    Environment.SetEnvironmentVariable(name, value)

        testList "redact" [
            for name, secrets, input, expected in redactCases ->
                testCase name <| fun () -> Expect.equal (GitProcess.redact secrets input) expected "redacted text"
        ]

        testList "classify" [
            for name, stderr, expected in classifyCases ->
                testCase name <| fun () ->
                    let actual =
                        match Checkout.classify stderr with
                        | Unauthorized _ -> "Unauthorized"
                        | NotFound _ -> "NotFound"
                        | Network _ -> "Network"
                        | GitFailed _ -> "GitFailed"
                        | other -> sprintf "%A" other

                    Expect.equal actual expected "category"
        ]

        testList "lfsEndpoint" [
            for name, remote, expected in lfsEndpointCases ->
                testCase name <| fun () -> Expect.equal (Checkout.lfsEndpoint remote) expected "endpoint"
        ]

        testList "clone URL validation" [
            for name, host, raw, expected in cloneUrlCases ->
                testCase name <| fun () -> Expect.equal (Checkout.validateCloneUrl host raw) expected "validated URL"
        ]

        testList "checkout input validation" [
            for name, host, projectId, token, sha, expected in inputCases do
                testCase name <| fun () ->
                    let error =
                        failureOf (fun () ->
                            Checkout.checkout (host, projectId, token, sha, [], timeout, CancellationToken.None) |> wait)

                    Expect.equal error.Error expected "error"

            testCase "a pattern with a comma is rejected before any work" <| fun () ->
                Expect.throwsT<ArgumentException>
                    (fun () ->
                        Checkout.checkout ("gitdev.example.org", 1, "tok", null, [ "workflows/{a,b}" ], timeout, CancellationToken.None)
                        |> wait
                        |> ignore)
                    "comma in a pattern"

            testCase "a negative timeout is rejected before any work" <| fun () ->
                Expect.throwsT<ArgumentOutOfRangeException>
                    (fun () ->
                        Checkout.checkout ("gitdev.example.org", 1, "tok", null, [], TimeSpan.FromSeconds -1., CancellationToken.None)
                        |> wait
                        |> ignore)
                    "negative timeout"
        ]

        testCase "an expired timeout reports TimedOut and deletes the temporary root" <| fun () ->
            use scratch = newScratch ()
            twoCommitRepo scratch |> ignore

            let error =
                failureOf (fun () ->
                    Checkout.checkoutRemote
                        scratch.Out
                        (fileUrl scratch.Repo)
                        None
                        None
                        None
                        []
                        (TimeSpan.FromMilliseconds 1.)
                        CancellationToken.None
                    |> wait)

            Expect.equal error.Error TimedOut "the error"
            Expect.isEmpty (Directory.GetFileSystemEntries scratch.Out) "the temporary root is deleted"

        testCase "a cancelled token raises OperationCanceledException and deletes the temporary root" <| fun () ->
            use scratch = newScratch ()
            twoCommitRepo scratch |> ignore
            use cancelled = new CancellationTokenSource()
            cancelled.Cancel()

            let raised =
                try
                    Checkout.checkoutRemote scratch.Out (fileUrl scratch.Repo) None None None [] timeout cancelled.Token
                    |> wait
                    |> ignore

                    false
                with :? OperationCanceledException ->
                    true

            Expect.isTrue raised "the caller's cancellation surfaces as OperationCanceledException"
            Expect.isEmpty (Directory.GetFileSystemEntries scratch.Out) "the temporary root is deleted"

        testCase "sweepStaleRoots deletes only old datahubgit-checkout folders and counts them" <| fun () ->
            use scratch = newScratch ()

            let make (name: string) (age: TimeSpan) =
                let dir = Path.Combine(scratch.Out, name)
                Directory.CreateDirectory(Path.Combine(dir, "repo")) |> ignore
                let objectFile = Path.Combine(dir, "repo", "object")
                File.WriteAllText(objectFile, "x")
                File.SetAttributes(objectFile, FileAttributes.ReadOnly)
                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow - age)
                dir

            let stale = make "datahubgit-checkout-stale" (TimeSpan.FromHours 3.)
            let fresh = make "datahubgit-checkout-fresh" (TimeSpan.FromMinutes 5.)
            let foreign = make "other-stale" (TimeSpan.FromHours 3.)
            Expect.equal (Checkout.sweepStaleRootsIn scratch.Out (TimeSpan.FromHours 1.)) 1 "one folder deleted"
            Expect.isFalse (Directory.Exists stale) "the stale checkout is gone, read-only files included"
            Expect.isTrue (Directory.Exists fresh) "a recent checkout stays"
            Expect.isTrue (Directory.Exists foreign) "a folder with another name stays"

        testCase "an empty include list keeps every LFS file a pointer and installs no LFS filter" <| fun () ->
            use scratch = newScratch ()
            lfsRepo scratch []
            use checkout = checkoutOf scratch None None None []

            for relative in [ "workflows/w/model.bin"; "runs/r/data.bin" ] do
                Expect.stringStarts
                    (File.ReadAllText(Path.Combine(checkout.Directory, relative)))
                    "version https://git-lfs.github.com/spec/v1"
                    $"{relative} stays a pointer"

            let gitConfig = File.ReadAllText(Path.Combine(checkout.Directory, ".git", "config"))
            Expect.isFalse (gitConfig.Contains "filter \"lfs\"") "git-lfs was not installed into the checkout"

        testCase "a hostile .lfsconfig in the ARC cannot redirect the LFS download" <| fun () ->
            use scratch = newScratch ()

            lfsRepo
                scratch
                [ ".lfsconfig",
                  "[lfs]\n\turl = http://127.0.0.1:9/internal-service\n\tfetchexclude = workflows/**\n"
                  + "[remote \"origin\"]\n\tlfsurl = http://127.0.0.1:9/x\n" ]

            use checkout =
                checkoutOf scratch (Some "gitlab.example.org") (Some testToken) None [ "workflows/**" ]

            Expect.equal
                (File.ReadAllText(Path.Combine(checkout.Directory, "workflows", "w", "model.bin")))
                "model content\n"
                "the workflow file is hydrated from the real remote"

            Expect.stringStarts
                (File.ReadAllText(Path.Combine(checkout.Directory, "runs", "r", "data.bin")))
                "version https://git-lfs.github.com/spec/v1"
                "the run file stays a pointer"

        testCase "LFS files under the requested path are hydrated and others stay pointers" <| fun () ->
            use scratch = newScratch ()
            lfsRepo scratch []
            use checkout = checkoutOf scratch None None None [ "workflows/**" ]

            Expect.equal
                (File.ReadAllText(Path.Combine(checkout.Directory, "workflows", "w", "model.bin")))
                "model content\n"
                "the workflow file has its real content"

            Expect.stringStarts
                (File.ReadAllText(Path.Combine(checkout.Directory, "runs", "r", "data.bin")))
                "version https://git-lfs.github.com/spec/v1"
                "the run file stays a pointer"
    ]
