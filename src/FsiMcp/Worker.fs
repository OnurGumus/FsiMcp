/// The worker process (`fsi-mcp --worker`): hosts one FsiEvaluationSession and answers JSON-lines requests
/// from the server on stdin/stdout. It is disposable: the server replaces it on load_project/reset and kills
/// it on timeout, which is the only reliable way to unload assemblies or stop runaway code in .NET.
module FsiMcp.Worker

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.InteropServices
open System.Runtime.Loader
open System.Text
open System.Text.Json
open System.Threading
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Interactive.Shell
open FSharp.Compiler.Symbols
open FsiMcp.Text

let private managedByName = ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
let private nativeByName = ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase)

/// Runtime binding for the referenced closure: managed by simple name, native by library-name variants.
let private installResolvers () =
    AssemblyLoadContext.Default.add_Resolving(
        Func<AssemblyLoadContext, AssemblyName, Assembly>(fun ctx name ->
            match managedByName.TryGetValue name.Name with
            | true, p -> ctx.LoadFromAssemblyPath p
            | _ -> null))
    AssemblyLoadContext.Default.add_ResolvingUnmanagedDll(
        Func<Assembly, string, nativeint>(fun _ name ->
            [ name; name + ".dylib"; "lib" + name + ".dylib"; name + ".so"; "lib" + name + ".so"; name + ".dll" ]
            |> List.tryPick (fun c ->
                match nativeByName.TryGetValue c with
                | true, p -> Some p
                | _ -> None)
            |> Option.map NativeLibrary.Load
            |> Option.defaultValue IntPtr.Zero))

let private severity (d: FSharpDiagnostic) =
    match d.Severity with
    | FSharpDiagnosticSeverity.Error -> "error"
    | FSharpDiagnosticSeverity.Warning -> "warning"
    | FSharpDiagnosticSeverity.Info -> "info"
    | _ -> "hidden"

let private formatDiagnostics (diags: FSharpDiagnostic[]) =
    diags
    |> Array.filter (fun d -> d.Severity <> FSharpDiagnosticSeverity.Hidden)
    |> Array.map (fun d -> $"({d.StartLine},{d.StartColumn + 1}): {severity d} {d.ErrorNumberText}: {d.Message}")
    |> String.concat "\n"

let rec private innermost (e: exn) =
    match e with
    | :? TargetInvocationException as t when not (isNull t.InnerException) -> innermost t.InnerException
    | :? AggregateException as a when a.InnerExceptions.Count = 1 -> innermost a.InnerExceptions[0]
    | e -> e

let formatException (e: exn) =
    let e = innermost e
    let stack =
        (if isNull e.StackTrace then "" else e.StackTrace).Split('\n')
        |> Array.map (fun l -> l.TrimEnd('\r'))
        |> Array.filter (fun l -> not (l.TrimStart().StartsWith "at System.Reflection."))
        |> Array.truncate 12
        |> String.concat "\n"
    $"{e.GetType().FullName}: {e.Message}\n{stack}"

/// The default host config with output sized for an LLM. FSI normally echoes every binding's VALUE: a list
/// of 24 nested records rendered as ~21K chars, context the agent pays for on every call.
type private CompactConfig(inner: FsiEvaluationSessionHostConfig) =
    inherit FsiEvaluationSessionHostConfig()
    override _.FormatProvider = inner.FormatProvider
    override _.FloatingPointFormat = inner.FloatingPointFormat
    override _.AddedPrinters = inner.AddedPrinters
    // `let x = ...` echoes only `val x: T`; evaluate an expression to see a value.
    override _.ShowDeclarationValues = false
    override _.ShowIEnumerable = inner.ShowIEnumerable
    override _.ShowProperties = inner.ShowProperties
    override _.PrintSize = 600
    override _.PrintDepth = 6
    override _.PrintWidth = 140
    override _.PrintLength = 20
    override _.ReportUserCommandLineArgs args = inner.ReportUserCommandLineArgs args
    override _.GetOptionalConsoleReadLine probe = inner.GetOptionalConsoleReadLine probe
    override _.StartServer name = inner.StartServer name
    override _.EventLoopRun() = inner.EventLoopRun()
    override _.EventLoopInvoke(f) = inner.EventLoopInvoke f
    override _.EventLoopScheduleRestart() = inner.EventLoopScheduleRestart()
    override _.UseFsiAuxLib = inner.UseFsiAuxLib

type private Session(refs: string list, outCap: Capture, errCap: Capture) =
    let fsi =
        let argv =
            Array.append [| "fsi"; "--noninteractive"; "--nologo" |] (refs |> List.map (fun r -> "-r:" + r) |> Array.ofList)
        let config = CompactConfig(FsiEvaluationSession.GetDefaultConfiguration())
        FsiEvaluationSession.Create(config, argv, new StringReader(""), outCap, errCap)
    do
        outCap.Drain() |> ignore
        errCap.Drain() |> ignore

    member this.Eval(code: string) : string * bool =
        let text, isError, _ = this.EvalWithStats code
        text, isError

    /// Eval, plus what it cost: (elapsed ms, bytes allocated, GC counts gen0/1/2).
    member _.EvalWithStats(code: string) : string * bool * (float * float * int[]) =
        let background = outCap.Drain() + errCap.Drain()
        let mutable result = Unchecked.defaultof<Choice<FsiValue option, exn> * FSharpDiagnostic[]>
        let mutable hostError: exn = null
        let gcBefore = [| GC.CollectionCount 0; GC.CollectionCount 1; GC.CollectionCount 2 |]
        let allocBefore = GC.GetTotalAllocatedBytes true
        let sw = Stopwatch.StartNew()
        // Deep recursion is common in F#; give evaluations a big stack.
        let t =
            Thread(
                ThreadStart(fun () ->
                    try result <- fsi.EvalInteractionNonThrowing code
                    with e -> hostError <- e),
                256 * 1024 * 1024)
        t.Start()
        t.Join()
        let elapsed = sw.Elapsed.TotalSeconds
        let allocated = float (GC.GetTotalAllocatedBytes true - allocBefore)
        let gcs = [| for g in 0..2 -> GC.CollectionCount g - gcBefore[g] |]
        let out = outCap.Drain()
        // With ShowDeclarationValues=false FSI also drops the trailing value when the interaction has
        // several statements (`printfn ..; xs` echoes only `val it: int list`). Put the value back.
        let out =
            match result with
            | (Choice1Of2(Some v), _) when isNull hostError && v.ReflectionType <> typeof<unit> ->
                let lines = out.TrimEnd().Split('\n')
                match lines |> Array.tryFindIndexBack (fun l -> l.StartsWith "val it:") with
                | Some i when lines[i].Contains(" = ") || lines[i].EndsWith(" =") -> out
                | found ->
                    let formatted =
                        try fsi.FormatValue(v.ReflectionValue, v.ReflectionType).Replace("\r\n", "\n")
                        with e -> $"<could not format: {e.Message}>"
                    let formatted =
                        if formatted.Contains('\n') then "\n  " + formatted.Replace("\n", "\n  ") else " " + formatted
                    match found with
                    | Some i ->
                        lines[i] <- lines[i] + " =" + formatted
                        String.Join("\n", lines)
                    | None -> out.TrimEnd() + "\nval it =" + formatted
            | _ -> out
        let err = errCap.Drain().Replace("Stopped due to error\n", "").Replace("Stopped due to error", "")
        let sb = StringBuilder()
        let add (s: string) = if s <> "" then sb.Append(s).Append('\n') |> ignore
        if background.Trim() <> "" then
            add "[output written between calls, e.g. by background threads]"
            add (clip 2000 (background.TrimEnd()))
            add "[this call]"
        add (clip 8000 (out.TrimEnd()))
        if err.Trim() <> "" then
            add "stderr:"
            add (clip 4000 (err.TrimEnd()))
        let isError =
            if not (isNull hostError) then
                add ("host error: " + formatException hostError)
                true
            else
                let value, diags = result
                let isError =
                    match value with
                    | Choice1Of2 _ -> false
                    | Choice2Of2 e ->
                        let hasCompileError = diags |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
                        if not hasCompileError then add (formatException e)
                        true
                add (formatDiagnostics diags)
                // FSI commits an interaction all or nothing: a failure anywhere drops every
                // binding, open and #load in it, which is easy to miss when it ran halfway.
                if isError then
                    add "Nothing from this eval was kept (bindings, opens, #load): fix it and send all of it again."
                isError
        // Cost of the eval itself, when it is worth reading: allocation volume is a far steadier
        // A/B signal than time, and the GC counts say how much of the time was collection.
        if elapsed >= 0.5 || allocated >= 100e6 then
            add $"[{elapsed:F2} s · {formatBytes allocated} allocated · GCs gen0 {gcs[0]} / gen1 {gcs[1]} / gen2 {gcs[2]}]"
        let text = sb.ToString().TrimEnd()
        (if text = "" then "ok (no output)" else text), isError, (elapsed * 1000.0, allocated, gcs)

    /// What the session has bound so far: name and type.
    member _.Bindings() : string * bool =
        let ctx = FSharpDisplayContext.Empty.WithShortTypeNames true
        match fsi.GetBoundValues() with
        | [] -> "No bindings yet.", false
        | values ->
            values
            |> List.map (fun b ->
                let ty = try b.Value.FSharpType.Format ctx with _ -> b.Value.ReflectionType.Name
                $"{b.Name}: {ty}")
            |> String.concat "\n"
            |> clip 6000,
            false

    /// `#load` a script, then open its module and replay its top-level `open`s in the session, so its
    /// helpers are usable unqualified, as if typed in. Each step is its own interaction: a failing
    /// open cannot roll back the load.
    member this.LoadPrelude(path: string) : string * bool =
        let name = Path.GetFileName path
        if not (File.Exists path) then $"prelude {name}: NOT FOUND at {path}", false
        else
        let loadText, loadFailed = this.Eval($"#load @\"{path}\"")
        if loadFailed then $"prelude {name}: FAILED to load\n{clip 3000 loadText}", false
        else
        let lines = File.ReadAllLines path
        let explicitModule =
            lines
            |> Array.tryPick (fun l ->
                let t = l.Trim()
                if t.StartsWith "module " && not (t.Contains "=") then Some(t.Substring(7).Trim()) else None)
        let scriptModule =
            match explicitModule with
            | Some m -> m
            | None ->
                let n = Path.GetFileNameWithoutExtension path
                let n = string (Char.ToUpperInvariant n[0]) + n.Substring 1
                if n |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_') then n else $"``{n}``"
        let opens =
            lines
            |> Array.choose (fun l -> if l.StartsWith "open " then Some(l.Substring(5).Trim()) else None)
            |> List.ofArray
        let results = (scriptModule :: opens) |> List.distinct |> List.map (fun o -> o, snd (this.Eval($"open {o}")))
        let opened = results |> List.filter (snd >> not) |> List.map fst
        let failed = results |> List.filter snd |> List.map fst
        let failedText = if failed.IsEmpty then "" else "; could not open " + String.Join(", ", failed)
        let openedText = String.Join(", ", opened)
        $"prelude {name}: loaded; opened {openedText}{failedText}", true

    member _.Typecheck(code: string) : string * bool =
        let parse, check, _ = fsi.ParseAndCheckInteraction code
        let diags = check.Diagnostics
        let hasErrors = diags |> Array.exists (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
        let sigs =
            if hasErrors then []
            else
                try
                    let ctx = FSharpDisplayContext.Empty.WithShortTypeNames true
                    // The signature also holds every earlier interaction; keep what the checked text declares.
                    let mine (r: FSharp.Compiler.Text.range) = r.FileName = parse.FileName
                    [ for ent in check.PartialAssemblySignature.Entities do
                        for m in ent.MembersFunctionsAndValues do
                            if not m.IsCompilerGenerated && mine m.DeclarationLocation then
                                yield $"val {m.DisplayName}: {m.FullType.Format ctx}"
                        for n in ent.NestedEntities do
                            if mine n.DeclarationLocation then yield $"type {n.DisplayName}" ]
                with _ -> []
        let parts = [ String.Join("\n", sigs); formatDiagnostics diags ] |> List.filter ((<>) "")
        let text = String.Join("\n", parts)
        (if text = "" then "ok (no errors)" else text), hasErrors

/// Worker entry point. Requests: {op: init, refs, native} | {op: eval, code} | {op: typecheck, code};
/// every reply is {text, isError}.
let run () =
    let protocolOut = new StreamWriter(Console.OpenStandardOutput(), UTF8Encoding(false))
    let protocolIn = new StreamReader(Console.OpenStandardInput(), UTF8Encoding(false))
    let outCap = new Capture()
    let errCap = new Capture()
    Console.SetOut outCap
    Console.SetError errCap
    Console.SetIn(new StringReader(""))
    installResolvers ()
    let reply (text: string, isError: bool) = writeLine protocolOut {| text = text; isError = isError |}
    let replyWith (text: string, isError: bool) (extra: (string * obj) list) =
        let d = Collections.Generic.Dictionary<string, obj>()
        d["text"] <- box text
        d["isError"] <- box isError
        for k, v in extra do d[k] <- v
        writeLine protocolOut d
    let mutable session: Session option = None
    let mutable line = protocolIn.ReadLine()
    while not (isNull line) do
        try
            use doc = JsonDocument.Parse line
            let r = doc.RootElement
            match str r "op", session with
            | "init", _ ->
                let sw = Stopwatch.StartNew()
                let refs = r.GetProperty("refs").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
                for p in refs do
                    managedByName[Path.GetFileNameWithoutExtension p] <- p
                for n in r.GetProperty("native").EnumerateArray() do
                    nativeByName[n[0].GetString()] <- n[1].GetString()
                let s = Session(refs, outCap, errCap)
                s.Eval "()" |> ignore // pay the compiler's JIT now, not on the first real eval
                session <- Some s
                let gc = if Runtime.GCSettings.IsServerGC then "server GC" else "workstation GC"
                let ready = $"ready in {sw.Elapsed.TotalSeconds:F1}s (pid {Environment.ProcessId}, .NET {Environment.Version}, {gc})"
                match str r "prelude" with
                | "" -> reply (ready, false)
                | path ->
                    let message, loaded = s.LoadPrelude path
                    replyWith (ready + "\n" + message, false) [ "prelude", box {| loaded = loaded; message = message |} ]
            | "eval", Some s ->
                let text, isError, (ms, bytes, gcs) = s.EvalWithStats(str r "code")
                replyWith (text, isError) [ "stats", box {| elapsedMs = ms; allocatedBytes = bytes; gen0 = gcs[0]; gen1 = gcs[1]; gen2 = gcs[2] |} ]
            | "typecheck", Some s -> reply (s.Typecheck(str r "code"))
            | "bindings", Some s -> reply (s.Bindings())
            | op, _ -> reply ($"worker: bad op '{op}' (or no session)", true)
        with e -> reply ($"worker error: {formatException e}", true)
        line <- protocolIn.ReadLine()
    0
