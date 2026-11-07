/// The MCP server: newline-delimited JSON-RPC on stdin/stdout. It resolves projects with MSBuild and owns
/// the worker process that hosts F# Interactive.
module FsiMcp.Server

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open FsiMcp.Text
open FsiMcp.ProjectResolver

let version =
    let a = Assembly.GetExecutingAssembly()
    match a.GetCustomAttribute<AssemblyInformationalVersionAttribute>() with
    | null -> string (a.GetName().Version)
    | v -> v.InformationalVersion.Split('+').[0]

/// How to start this same program again: the tool shim / apphost, or `dotnet FsiMcp.dll`.
let private selfCommand () =
    let entry = Assembly.GetEntryAssembly().Location
    let pp = Environment.ProcessPath
    if Path.GetFileNameWithoutExtension(pp).Equals("dotnet", StringComparison.OrdinalIgnoreCase) then pp, [ entry; "--worker" ]
    else pp, [ "--worker" ]

type private WorkerProc(cwd: string, log: TextWriter) =
    let p =
        let exe, args = selfCommand ()
        let psi = ProcessStartInfo(exe)
        for a in args do psi.ArgumentList.Add a
        psi.UseShellExecute <- false
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.StandardInputEncoding <- UTF8Encoding(false)
        psi.StandardOutputEncoding <- UTF8Encoding(false)
        psi.WorkingDirectory <- cwd
        Process.Start psi
    // Only fatal runtime messages (e.g. "Stack overflow." + the repeating frame) reach the worker's real stderr.
    let crash = ResizeArray<string>()
    do
        let t =
            Thread(
                ThreadStart(fun () ->
                    let mutable l = p.StandardError.ReadLine()
                    while not (isNull l) do
                        log.WriteLine("[worker] " + l)
                        lock crash (fun () -> if crash.Count < 8 then crash.Add l)
                        l <- p.StandardError.ReadLine())
            )
        t.IsBackground <- true
        t.Start()

    /// None = no answer within the timeout (or cancelled).
    member _.Request(msg: obj, timeout: TimeSpan, ct: CancellationToken) : (string * bool) option =
        writeLine p.StandardInput msg
        let read = p.StandardOutput.ReadLineAsync()
        if Task.WaitAny([| read :> Task; Task.Delay(timeout, ct) |]) <> 0 then None
        else
            match read.Result with
            | null ->
                p.WaitForExit(2000) |> ignore
                Thread.Sleep 100 // let the stderr pump catch the crash message
                let exitCode = if p.HasExited then string p.ExitCode else "?"
                let detail = lock crash (fun () -> String.Join("\n", crash))
                Some(
                    $"The worker process died (exit code {exitCode}). State is lost; the next call starts a fresh session.\n{detail}"
                        .TrimEnd(),
                    true
                )
            | line ->
                use d = JsonDocument.Parse line
                Some(str d.RootElement "text", d.RootElement.GetProperty("isError").GetBoolean())

    member _.Alive = not p.HasExited

    member _.Kill() =
        try
            p.Kill(true)
            p.WaitForExit(5000) |> ignore // release file handles before the snapshot is deleted
        with _ -> ()

type private Front(log: TextWriter) =
    let cwd = Environment.CurrentDirectory
    let mutable loaded: Resolved option = None
    let mutable snapshotDir: string option = None
    let mutable worker: WorkerProc option = None

    let kill () =
        worker |> Option.iter (fun w -> w.Kill())
        worker <- None

    let spawn () : Result<string, string> =
        kill ()
        let w = WorkerProc(cwd, log)
        let refs, native =
            match loaded with
            | Some r -> r.Managed, r.Native |> List.map (fun (a, b) -> [| a; b |])
            | None -> [], []
        match w.Request({| op = "init"; refs = refs; native = native |}, TimeSpan.FromSeconds 120., CancellationToken.None) with
        | Some(text, false) ->
            worker <- Some w
            Ok text
        | Some(text, true) ->
            w.Kill()
            Error text
        | None ->
            w.Kill()
            Error "worker did not start within 120 s"

    let ensure () =
        match worker with
        | Some w when w.Alive -> Ok w
        | _ -> spawn () |> Result.map (fun _ -> worker.Value)

    let request (msg: obj) (timeout: TimeSpan) (ct: CancellationToken) =
        match ensure () with
        | Error e -> $"Could not start the F# session: {e}", true
        | Ok w ->
            match w.Request(msg, timeout, ct) with
            | Some r ->
                if not w.Alive then worker <- None
                r
            | None ->
                let why = if ct.IsCancellationRequested then "was cancelled" else $"timed out after {timeout.TotalSeconds:F0}s"
                let restart =
                    match spawn () with
                    | Ok t ->
                        let refs = if loaded.IsSome then "; project references re-applied" else ""
                        $"A fresh session is ready ({t}{refs}). All earlier bindings are lost."
                    | Error e -> $"Restarting failed: {e}"
                $"Evaluation {why}; the worker process was killed. {restart}", true

    member _.Warm() = ensure () |> ignore

    member _.Shutdown() =
        kill ()
        snapshotDir |> Option.iter Snapshot.delete

    member _.Eval(code: string, timeoutSeconds: int, ct) =
        request {| op = "eval"; code = code |} (TimeSpan.FromSeconds(float timeoutSeconds)) ct

    member _.Typecheck(code: string, ct) =
        request {| op = "typecheck"; code = code |} (TimeSpan.FromSeconds 120.) ct

    member _.Reset() =
        match spawn () with
        | Ok t ->
            let refs =
                match loaded with
                | Some r -> $", referencing {Path.GetFileName r.ProjectFile} ({r.Managed.Length} assemblies, not rebuilt)"
                | None -> ""
            $"Fresh session ({t}{refs}).", false
        | Error e -> e, true

    member _.LoadProject(path: string, build: bool, configuration: string) =
        let sw = Stopwatch.StartNew()
        match findProjectFile path |> Result.bind (fun p -> resolveProject p build configuration) with
        | Error e -> e, true // keep the current session alive
        | Ok r ->
            let resolveSecs = sw.Elapsed.TotalSeconds
            let dir, copies = Snapshot.create r.Built
            let swap p = Map.tryFind p copies |> Option.defaultValue p
            loaded <- Some { r with Managed = r.Managed |> List.map swap }
            let previous = snapshotDir
            snapshotDir <- Some dir
            let started = spawn () // kills the previous worker first
            previous |> Option.iter Snapshot.delete
            match started with
            | Error e -> $"Resolved {r.Managed.Length} assemblies but the session failed to start:\n{e}", true
            | Ok t ->
                let surface = publicSurface r.TargetPath
                let sb = StringBuilder()
                let line (s: string) = sb.Append(s).Append('\n') |> ignore
                let what = if build then $"{configuration} build + resolve" else "resolve (no build)"
                line $"Loaded {Path.GetFileName r.ProjectFile}: {what} {resolveSecs:F1}s, session {t}. References {r.Managed.Length} assemblies, {r.Native.Length} native libs."
                line $"Output: {r.TargetPath}"
                for n in r.Notes do line n
                line $"Public top-level types/modules in {Path.GetFileName r.TargetPath} ({surface.Length}):"
                sb.Append(clip 3000 (String.Join("\n", surface))) |> ignore
                sb.ToString(), false

let private toolsJson =
    JsonDocument.Parse(
        """
[
  { "name": "eval",
    "description": "Evaluate F# code in a persistent F# Interactive session (like dotnet fsi). Bindings, types, opens and #r persist across calls until reset/load_project. Returns printed output, `val x: T` for each binding (types only; evaluate an expression to see a value), `val it: T = value` for a trailing expression, and compiler diagnostics. Values print with limits and long output is clipped: project the fields you need (List.map) instead of printing large records. Expensive results stay in memory, so bind them once and inspect them in later calls. Do not end code with ;;",
    "inputSchema": { "type": "object", "properties": {
        "code": { "type": "string", "description": "F# code: declarations, expressions, #r/#load/open directives" },
        "timeout_seconds": { "type": "integer", "description": "Kill the session after this many seconds (default 120); state is lost" } },
      "required": ["code"] } },
  { "name": "typecheck",
    "description": "Type-check F# code against the current session (loaded project + earlier bindings) WITHOUT running it or binding anything. Returns inferred signatures, or compiler diagnostics.",
    "inputSchema": { "type": "object", "properties": { "code": { "type": "string" } }, "required": ["code"] } },
  { "name": "reset",
    "description": "Discard all session state (bindings, opens, loaded assemblies) by starting a fresh session process; keeps the references from the last load_project without rebuilding.",
    "inputSchema": { "type": "object", "properties": {} } },
  { "name": "load_project",
    "description": "Build a .fsproj/.csproj and start a fresh session referencing its output assembly and full runtime closure (NuGet packages, project references, native libs, ASP.NET Core shared framework), so its modules can be opened and called from eval. Call again after editing the project's source: the new session sees the rebuilt code. Lists the assembly's public top-level modules.",
    "inputSchema": { "type": "object", "properties": {
        "path": { "type": "string", "description": "Project file, or a directory containing exactly one project" },
        "build": { "type": "boolean", "description": "Build first (default true). false = use the existing bin output" },
        "configuration": { "type": "string", "description": "Build configuration (default Debug)" } },
      "required": ["path"] } }
]
"""
    )
        .RootElement.Clone()

let private instructions =
    "Persistent F# Interactive (FSI) session. Use load_project to make a project's code available (and again after editing it), then eval to run F# against it; state persists across eval calls. Use typecheck to check code without executing it. Prefer this over writing throwaway console projects or scripts for probing F# code."

/// Server entry point: serve MCP on stdin/stdout until stdin closes.
let run () =
    let protocolOut = new StreamWriter(Console.OpenStandardOutput(), UTF8Encoding(false))
    let protocolIn = new StreamReader(Console.OpenStandardInput(), UTF8Encoding(false))
    let log = new StreamWriter(Console.OpenStandardError(), UTF8Encoding(false))
    log.AutoFlush <- true
    Console.SetOut(TextWriter.Null)
    Console.SetIn(new StringReader(""))
    Snapshot.sweepStale ()
    let send (msg: obj) = writeLine protocolOut msg
    let front = Front(log)
    let gate = new SemaphoreSlim(1, 1)
    let inflight = ConcurrentDictionary<string, CancellationTokenSource>()

    let serialized (f: unit -> unit) =
        Task.Run(
            Action(fun () ->
                gate.Wait()
                try
                    f ()
                finally
                    gate.Release() |> ignore)
        )
        |> ignore

    let callTool (name: string) (a: JsonElement) (ct: CancellationToken) =
        match name with
        | "eval" -> front.Eval(str a "code", argInt a "timeout_seconds" 120, ct)
        | "typecheck" -> front.Typecheck(str a "code", ct)
        | "reset" -> front.Reset()
        | "load_project" ->
            let path = match str a "path" with "" -> "." | p -> p
            let config = match str a "configuration" with "" -> "Debug" | c -> c
            front.LoadProject(path, argBool a "build" true, config)
        | other -> $"Unknown tool: {other}", true

    let handle (line: string) =
        use doc = JsonDocument.Parse line
        let root = doc.RootElement
        let meth = str root "method"
        let id =
            match root.TryGetProperty "id" with
            | true, v -> Some(v.Clone())
            | _ -> None
        let prms =
            match root.TryGetProperty "params" with
            | true, v -> v.Clone()
            | _ -> emptyObject
        match meth, id with
        | "initialize", Some id ->
            let ver = match str prms "protocolVersion" with "" -> "2025-06-18" | v -> v
            send
                {| jsonrpc = "2.0"
                   id = id
                   result =
                    {| protocolVersion = ver
                       capabilities = {| tools = {| listChanged = false |} |}
                       serverInfo = {| name = "fsi-mcp"; version = version |}
                       instructions = instructions |} |}
            serialized front.Warm // start the worker now so the first eval is warm
        | "ping", Some id -> send {| jsonrpc = "2.0"; id = id; result = Dictionary<string, obj>() |}
        | "tools/list", Some id -> send {| jsonrpc = "2.0"; id = id; result = {| tools = toolsJson |} |}
        | "tools/call", Some id ->
            let key = id.GetRawText()
            let cts = new CancellationTokenSource()
            inflight[key] <- cts
            let name = str prms "name"
            let args =
                match prms.TryGetProperty "arguments" with
                | true, v -> v.Clone()
                | _ -> emptyObject
            serialized (fun () ->
                let sw = Stopwatch.StartNew()
                let text, isError =
                    try
                        callTool name args cts.Token
                    with e ->
                        $"internal error: {e}", true
                inflight.TryRemove key |> ignore
                log.WriteLine($"[fsi-mcp] {name} {sw.ElapsedMilliseconds} ms isError={isError}")
                send
                    {| jsonrpc = "2.0"
                       id = id
                       result = {| content = [| {| ``type`` = "text"; text = text |} |]; isError = isError |} |})
        | "notifications/cancelled", None ->
            match prms.TryGetProperty "requestId" with
            | true, rid ->
                match inflight.TryGetValue(rid.GetRawText()) with
                | true, c -> c.Cancel()
                | _ -> ()
            | _ -> ()
        | _, None -> ()
        | m, Some id ->
            send {| jsonrpc = "2.0"; id = id; error = {| code = -32601; message = $"Method not found: {m}" |} |}

    let mutable line = protocolIn.ReadLine()
    while not (isNull line) do
        if not (String.IsNullOrWhiteSpace line) then
            try
                handle line
            with e ->
                log.WriteLine($"[fsi-mcp] bad message: {e.Message}")
        line <- protocolIn.ReadLine()
    gate.Wait() // let an in-flight call finish before tearing down
    front.Shutdown()
    0
