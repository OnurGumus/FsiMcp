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

    member _.Eval(code: string) : string * bool =
        let background = outCap.Drain() + errCap.Drain()
        let mutable result = Unchecked.defaultof<Choice<FsiValue option, exn> * FSharpDiagnostic[]>
        let mutable hostError: exn = null
        // Deep recursion is common in F#; give evaluations a big stack.
        let t =
            Thread(
                ThreadStart(fun () ->
                    try result <- fsi.EvalInteractionNonThrowing code
                    with e -> hostError <- e),
                256 * 1024 * 1024)
        t.Start()
        t.Join()
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
                isError
        let text = sb.ToString().TrimEnd()
        (if text = "" then "ok (no output)" else text), isError

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
                reply ($"ready in {sw.Elapsed.TotalSeconds:F1}s", false)
            | "eval", Some s -> reply (s.Eval(str r "code"))
            | "typecheck", Some s -> reply (s.Typecheck(str r "code"))
            | op, _ -> reply ($"worker: bad op '{op}' (or no session)", true)
        with e -> reply ($"worker error: {formatException e}", true)
        line <- protocolIn.ReadLine()
    0
