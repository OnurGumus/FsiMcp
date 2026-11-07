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

type private WorkerProc(cwd: string, log: TextWriter, env: (string * string) list) =
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
        for k, v in env do
            psi.Environment[k] <- v
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
    member _.Pid = p.Id

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
    // Remembered across load_project / reset / timeout restarts until changed.
    let mutable prelude: string option = None
    let mutable gcServer = false
    let mutable artifactsPath: string option = None
    let mutable extraEnv: (string * string) list = []
    let mutable lastSurface: (string * string list) option = None

    let workerEnv () =
        (if gcServer then [ "DOTNET_gcServer", "1" ] else []) @ extraEnv

    let kill () =
        worker |> Option.iter (fun w -> w.Kill())
        worker <- None

    let spawn () : Result<string, string> =
        kill ()
        let w = WorkerProc(cwd, log, workerEnv ())
        let refs, native =
            match loaded with
            | Some r -> r.Managed, r.Native |> List.map (fun (a, b) -> [| a; b |])
            | None -> [], []
        let init = {| op = "init"; refs = refs; native = native; prelude = Option.defaultValue "" prelude |}
        match w.Request(init, TimeSpan.FromSeconds 120., CancellationToken.None) with
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

    member _.Reset(newPrelude: string option) =
        newPrelude |> Option.iter (fun p -> prelude <- (if p = "" then None else Some(Path.GetFullPath p)))
        match spawn () with
        | Ok t ->
            let refs =
                match loaded with
                | Some r -> $", referencing {Path.GetFileName r.ProjectFile} ({r.Managed.Length} assemblies, not rebuilt)"
                | None -> ""
            $"Fresh session ({t}{refs}).", false
        | Error e -> e, true

    member _.LoadProject(path: string, build: bool, configuration: string, newPrelude: string option, newGcServer: bool option, newArtifactsPath: string option, newEnv: (string * string) list option) =
        newPrelude |> Option.iter (fun p -> prelude <- (if p = "" then None else Some(Path.GetFullPath p)))
        newGcServer |> Option.iter (fun g -> gcServer <- g)
        newArtifactsPath |> Option.iter (fun p -> artifactsPath <- (if p = "" then None else Some(Path.GetFullPath p)))
        newEnv |> Option.iter (fun e -> extraEnv <- e)
        let sw = Stopwatch.StartNew()
        match findProjectFile path |> Result.bind (fun p -> resolveProject p build configuration artifactsPath) with
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
                // `t` is the worker's ready line, then (with a prelude) the prelude's own line
                let ready, preludeLine =
                    match t.IndexOf '\n' with
                    | -1 -> t, ""
                    | i -> t.Substring(0, i), t.Substring(i + 1)
                line $"Loaded {Path.GetFileName r.ProjectFile}: {what} {resolveSecs:F1}s, session {ready}. References {r.Managed.Length} assemblies, {r.Native.Length} native libs."
                if preludeLine <> "" then line preludeLine
                line $"Output: {r.TargetPath}"
                for n in r.Notes do line n
                match lastSurface with
                | Some(p, s) when p = r.ProjectFile && s = surface ->
                    line $"Public top-level types/modules: unchanged ({surface.Length})."
                | _ ->
                    line $"Public top-level types/modules in {Path.GetFileName r.TargetPath} ({surface.Length}):"
                    sb.Append(clip 3000 (String.Join("\n", surface))) |> ignore
                lastSurface <- Some(r.ProjectFile, surface)
                sb.ToString().TrimEnd(), false

    member _.Bindings(ct) = request {| op = "bindings" |} (TimeSpan.FromSeconds 60.) ct

    /// Evaluate `code` while the server records the worker; append a CPU or allocation profile.
    member _.Profile(code: string, mode: Profiler.Mode, top: int, timeoutSeconds: int, ct) =
        match ensure () with
        | Error e -> $"Could not start the F# session: {e}", true
        | Ok w ->
            let attempt =
                try
                    Ok(Profiler.record w.Pid mode (fun () -> request {| op = "eval"; code = code |} (TimeSpan.FromSeconds(float timeoutSeconds)) ct))
                with e ->
                    Error $"Could not attach the profiler to the worker (pid {w.Pid}): {e.Message}"
            match attempt with
            | Error e -> e, true
            | Ok((text, isError), trace) ->
                let report =
                    try Profiler.analyze mode trace top
                    with e -> $"(could not analyse the trace: {e.Message})"
                let label = match mode with Profiler.Cpu -> "cpu" | Profiler.Alloc -> "alloc"
                $"{text}\n\n--- profile ({label}) ---\n{report}", isError

let private toolsJson =
    JsonDocument.Parse(
        """
[
  { "name": "eval",
    "description": "Evaluate F# code in a persistent F# Interactive session (like dotnet fsi). Bindings, types, opens and #r persist across calls until reset/load_project. Returns printed output, `val x: T` for each binding (types only; evaluate an expression to see a value), `val it: T = value` for a trailing expression, and compiler diagnostics; slow or allocation-heavy evals end with a [time · bytes allocated · GC counts] line. Values print with limits and long output is clipped: project the fields you need (List.map) instead of printing large records. Expensive results stay in memory, so bind them once and inspect them in later calls. An eval that fails keeps NOTHING from it (bindings, opens, #load), so put a #load in its own eval. Do not end code with ;;",
    "inputSchema": { "type": "object", "properties": {
        "code": { "type": "string", "description": "F# code: declarations, expressions, #r/#load/open directives" },
        "timeout_seconds": { "type": "integer", "description": "Kill the session after this many seconds (default 120); state is lost" } },
      "required": ["code"] } },
  { "name": "typecheck",
    "description": "Type-check F# code against the current session (loaded project + earlier bindings) WITHOUT running it or binding anything. Returns inferred signatures, or compiler diagnostics.",
    "inputSchema": { "type": "object", "properties": { "code": { "type": "string" } }, "required": ["code"] } },
  { "name": "reset",
    "description": "Discard all session state (bindings, opens, loaded assemblies) by starting a fresh session process; keeps the references and prelude from the last load_project without rebuilding.",
    "inputSchema": { "type": "object", "properties": {
        "prelude": { "type": "string", "description": "Change the prelude script (see load_project); \"\" removes it" } } } },
  { "name": "bindings",
    "description": "List what the session has bound so far (name: type).",
    "inputSchema": { "type": "object", "properties": {} } },
  { "name": "profile",
    "description": "Evaluate F# code like eval (same session, bindings persist) while recording a profile of it: mode cpu (default) = top methods by self time plus your code by total time, counting only threads running managed code; mode alloc = bytes allocated by type and by the nearest frame in your code. Use it to find where time or garbage goes before optimising.",
    "inputSchema": { "type": "object", "properties": {
        "code": { "type": "string", "description": "F# code to evaluate and profile" },
        "mode": { "type": "string", "enum": ["cpu", "alloc"], "description": "cpu (default) or alloc" },
        "top": { "type": "integer", "description": "Rows per table (default 15)" },
        "timeout_seconds": { "type": "integer", "description": "As for eval (default 120)" } },
      "required": ["code"] } },
  { "name": "load_project",
    "description": "Build a .fsproj/.csproj and start a fresh session referencing its output assembly and full runtime closure (NuGet packages, project references, native libs, ASP.NET Core shared framework), so its modules can be opened and called from eval. Call again after editing the project's source: the new session sees the rebuilt code. Lists the assembly's public top-level modules (again only when they change).",
    "inputSchema": { "type": "object", "properties": {
        "path": { "type": "string", "description": "Project file, or a directory containing exactly one project" },
        "build": { "type": "boolean", "description": "Build first (default true). false = use the existing bin output" },
        "configuration": { "type": "string", "description": "Build configuration (default Debug)" },
        "prelude": { "type": "string", "description": "An .fsx script to #load into every new session (after this load, reset and timeout restarts), with its module and top-level opens opened; \"\" removes it. Remembered until changed" },
        "gc_server": { "type": "boolean", "description": "Run the session with Server GC (like ASP.NET apps) instead of workstation GC; matters for timing. Remembered until changed" },
        "artifacts_path": { "type": "string", "description": "Directory for build output (bin, obj); nothing is written under the project. Remembered until changed; \"\" clears it" },
        "env": { "type": "object", "additionalProperties": { "type": "string" }, "description": "Environment variables for the session process. Remembered until changed" } },
      "required": ["path"] } }
]
"""
    )
        .RootElement.Clone()

let private instructions =
    "Persistent F# Interactive (FSI) session. Use load_project to make a project's code available (and again after editing it), then eval to run F# against it; state persists across eval calls. Use typecheck to check code without executing it. Prefer this over writing throwaway console projects or scripts for probing F# code."

/// A string argument that was given (possibly ""), or None when absent.
let private argStrOpt (a: JsonElement) (name: string) =
    if a.ValueKind <> JsonValueKind.Object then None
    else
        match a.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
        | _ -> None

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
        | "reset" -> front.Reset(argStrOpt a "prelude")
        | "load_project" ->
            let path = match str a "path" with "" -> "." | p -> p
            let config = match str a "configuration" with "" -> "Debug" | c -> c
            let gcServer =
                match a.TryGetProperty "gc_server" with
                | true, v when v.ValueKind = JsonValueKind.True -> Some true
                | true, v when v.ValueKind = JsonValueKind.False -> Some false
                | _ -> None
            let env =
                match a.TryGetProperty "env" with
                | true, v when v.ValueKind = JsonValueKind.Object ->
                    Some [ for p in v.EnumerateObject() -> p.Name, p.Value.ToString() ]
                | _ -> None
            front.LoadProject(path, argBool a "build" true, config, argStrOpt a "prelude", gcServer, argStrOpt a "artifacts_path", env)
        | "bindings" -> front.Bindings ct
        | "profile" ->
            let mode = match str a "mode" with "alloc" -> Profiler.Alloc | _ -> Profiler.Cpu
            front.Profile(str a "code", mode, argInt a "top" 15, argInt a "timeout_seconds" 120, ct)
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
