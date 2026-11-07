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

/// What `measure` found, per call.
type private Measurement =
    { MedianNs: float
      P90Ns: float
      BytesPerCall: float
      Gen0: int
      Gen1: int
      Gen2: int
      Calls: int }

/// The measuring loop, compiled per result type so calling the thunk never boxes or allocates.
type private Bench =
    /// Call `f` n times; the last result is returned so the caller can keep it alive.
    static member Calls<'T>(f: unit -> 'T, n: int) : 'T =
        let mutable last = Unchecked.defaultof<'T>
        for _ in 1..n do
            let r = f ()
            // A value type is kept in a local (boxing it would allocate on every call).
            if typeof<'T>.IsValueType then last <- r else GC.KeepAlive(box r)
        last

    /// Call `f` until tiered compilation has settled; returns the last result and the cost per call (ns).
    /// Tier-up happens in steps (tier 0, instrumented, tier 1 with dynamic PGO, each after a call count
    /// and a ~100 ms delay): four rounds of 30 calls and a pause, then `callMs` of calls.
    static member Warm<'T>(f: unit -> 'T, callMs: int) : 'T * float =
        let freq = Stopwatch.Frequency
        let mutable last = Unchecked.defaultof<'T>
        for _ in 1..4 do
            last <- Bench.Calls(f, 30)
            Thread.Sleep 150
        let start = Stopwatch.GetTimestamp()
        let mutable calls = 0
        let mutable chunk = 1
        // Short chunks (up to ~0.1 ms), so the loop around the calls is itself called often and tiers up.
        while Stopwatch.GetTimestamp() - start < freq * int64 callMs / 1000L do
            let t0 = Stopwatch.GetTimestamp()
            last <- Bench.Calls(f, chunk)
            calls <- calls + chunk
            if Stopwatch.GetTimestamp() - t0 < freq / 10_000L && chunk < 1_000_000 then chunk <- chunk * 2
        last, max 0.01 (float (Stopwatch.GetTimestamp() - start) * 1e9 / float freq / float calls)

    /// Warm up, size the batches, then run them until `iterations` calls (0 = for about 1 s, at least
    /// 10 calls). Call it on the thread whose allocations count.
    ///
    /// `copy` is the same expression compiled separately, warmed first and longer. With dynamic PGO, `f`
    /// is compiled at tier 1 once, early; if the code it calls (FSharp.Core and the like) has not settled
    /// by then, `f` keeps code that is slower and allocates more than what follows a while later. The first
    /// measure of `Array.init 100 id |> Array.sum` in a session read 416 B per call, every later one 0 B
    /// (the array is put on the stack); warming a copy for about 0.5 s more was enough, 1.3 s leaves margin.
    static member Run<'T>(copy: unit -> 'T, f: unit -> 'T, iterations: int) : Measurement =
        let freq = Stopwatch.Frequency
        let warmCopy, _ = Bench.Warm(copy, 1300)
        GC.KeepAlive(box warmCopy)
        let warm, estimateNs = Bench.Warm(f, 300)
        let mutable last = warm
        let auto = iterations <= 0
        let target = if auto then Int32.MaxValue else min 1_000_000 (max 10 iterations)
        // A batch of about 1 ms, and at least 10 batches in a run.
        let budgetCalls = if auto then max 1.0 (1e9 / estimateNs) else float target
        let batch = max 1 (int (min (ceil (1e6 / estimateNs)) (floor (budgetCalls / 10.0))))
        // Room for the batches the time budget allows (twice over, should the code get faster); a run
        // ends early if it fills up.
        let capacity =
            if auto then int (min 1e6 (2.0 * ceil (budgetCalls / float batch) + 10.0))
            else (target + batch - 1) / batch
        let perCallNs = Array.zeroCreate<float> capacity
        let tickNs = 1e9 / float freq
        let gc0, gc1, gc2 = GC.CollectionCount 0, GC.CollectionCount 1, GC.CollectionCount 2
        let bytesBefore = GC.GetAllocatedBytesForCurrentThread()
        let start = Stopwatch.GetTimestamp()
        let mutable calls = 0
        let mutable batches = 0
        let mutable running = true
        while running do
            let n = min batch (target - calls)
            let t0 = Stopwatch.GetTimestamp()
            last <- Bench.Calls(f, n)
            let t1 = Stopwatch.GetTimestamp()
            perCallNs[batches] <- float (t1 - t0) * tickNs / float n
            batches <- batches + 1
            calls <- calls + n
            running <-
                calls < target && batches < capacity && (not auto || calls < 10 || t1 - start < freq)
        let bytes = GC.GetAllocatedBytesForCurrentThread() - bytesBefore
        let gens = GC.CollectionCount 0 - gc0, GC.CollectionCount 1 - gc1, GC.CollectionCount 2 - gc2
        GC.KeepAlive(box last)
        let sorted = Array.sub perCallNs 0 batches |> Array.sort
        let median =
            if batches % 2 = 1 then sorted[batches / 2] else (sorted[batches / 2 - 1] + sorted[batches / 2]) / 2.0
        let p90 = sorted[max 0 (int (ceil (0.9 * float batches)) - 1)]
        let g0, g1, g2 = gens
        { MedianNs = median
          P90Ns = p90
          BytesPerCall = float bytes / float calls
          Gen0 = g0
          Gen1 = g1
          Gen2 = g2
          Calls = calls }

/// A time per call with its unit (ns, µs, ms, s), to 3 significant digits.
let private formatNs (ns: float) =
    let rounded =
        if ns <= 0.0 then 0.0
        else
            let scale = 10.0 ** (3.0 - floor (log10 ns) - 1.0)
            Math.Round(ns * scale) / scale
    let v, unit =
        if rounded < 1e3 then rounded, "ns"
        elif rounded < 1e6 then rounded / 1e3, "µs"
        elif rounded < 1e9 then rounded / 1e6, "ms"
        else rounded / 1e9, "s"
    let digits = if v >= 100.0 then "F0" elif v >= 10.0 then "F1" else "F2"
    v.ToString(digits, Globalization.CultureInfo.InvariantCulture) + " " + unit

let private formatBytesPerCall (b: float) =
    let s = b.ToString((if b >= 100.0 then "F0" else "0.##"), Globalization.CultureInfo.InvariantCulture)
    if s = "-0" then "0" else s

/// Prefix of the bindings `measure` makes; `bindings` never lists them.
let private measurePrefix = "__fsimcp_measure_"

type private Session(refs: string list, outCap: Capture, errCap: Capture) =
    let fsi =
        let argv =
            Array.append [| "fsi"; "--noninteractive"; "--nologo" |] (refs |> List.map (fun r -> "-r:" + r) |> Array.ofList)
        let config = CompactConfig(FsiEvaluationSession.GetDefaultConfiguration())
        FsiEvaluationSession.Create(config, argv, new StringReader(""), outCap, errCap)
    do
        outCap.Drain() |> ignore
        errCap.Drain() |> ignore
    let mutable measures = 0

    /// Run `f` on a fresh thread with a big stack (deep recursion is common in F#); rethrows its exception.
    let onBigStack (f: unit -> 'T) : 'T =
        let mutable result = Unchecked.defaultof<'T>
        let mutable error: exn = null
        let t = Thread(ThreadStart(fun () -> try result <- f () with e -> error <- e), 256 * 1024 * 1024)
        t.Start()
        t.Join()
        if not (isNull error) then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw()
        result

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

    /// Time and allocations per call of `expr`: bind it as a thunk under a hidden name, then run it many
    /// times on one thread. Prints nothing to the session's output; output from before it is kept for the
    /// next eval. Returns the text, the error flag, and the measurement (None on error).
    member _.Measure(expr: string, iterations: int) : string * bool * Measurement option =
        let pendingOut = outCap.Drain()
        let pendingErr = errCap.Drain()
        measures <- measures + 1
        let name = measurePrefix + string measures
        // The expression starts on line 4, indented by 12 columns; diagnostics are mapped back to it.
        let body =
            expr.Replace("\r\n", "\n").TrimEnd().Split('\n') |> Array.map (fun l -> "            " + l) |> String.concat "\n"
        let thunkNamed (binding: string) (name: string) (suffix: string) =
            $"{binding} {name} =\n    fun () ->\n        (\n{body}\n        ){suffix}"
        let diagnostics (diags: FSharpDiagnostic[]) =
            diags
            |> Array.filter (fun d -> d.Severity = FSharpDiagnosticSeverity.Error)
            |> Array.distinctBy (fun d -> d.StartLine, d.StartColumn, d.ErrorNumber, d.Message)
            |> Array.map (fun d ->
                let line, col =
                    if d.StartLine >= 4 && d.StartColumn >= 12 then d.StartLine - 3, d.StartColumn - 11
                    else d.StartLine, d.StartColumn + 1
                $"({line},{col}): error {d.ErrorNumberText}: {d.Message}")
            |> String.concat "\n"
        let thunk binding suffix = thunkNamed binding name suffix
        let fail (text: string) = text, true, None
        let outcome =
            try
                // Type-check the thunk first (nothing runs or is bound): its errors, and its result type.
                let parse, check, _ = fsi.ParseAndCheckInteraction(thunk "let" "")
                let shown = diagnostics (Array.append parse.Diagnostics check.Diagnostics)
                if shown <> "" then fail shown else
                let resultType =
                    try
                        check.PartialAssemblySignature.Entities
                        |> Seq.collect (fun e -> e.MembersFunctionsAndValues)
                        |> Seq.tryFind (fun m -> m.DisplayName = name && m.FullType.IsFunctionType)
                        |> Option.map (fun m -> m.FullType.GenericArguments[1])
                    with _ -> None
                let isFunction (t: FSharpType) = (if t.IsAbbreviation then t.StripAbbreviations() else t).IsFunctionType
                match resultType with
                | Some t when isFunction t ->
                    let shown = t.Format(FSharpDisplayContext.Empty.WithShortTypeNames true)
                    fail $"measure needs an expression that computes a value, not a function (this one has type {shown}). Apply it to its arguments, e.g. `f 42`."
                | _ ->
                // Bind it for real. `mutable` makes FSI keep the closure in a field, which GetBoundValues
                // can read (a plain `let f = fun () -> ...` compiles to a method). A generic result
                // (`failwith ..`, `[]`) cannot be a mutable's type: it is measured as obj instead.
                let eval code = onBigStack (fun () -> fsi.EvalInteractionNonThrowing code)
                // A generic struct (`Ok 5`, `ValueNone`) as obj would box on every call and read as allocation.
                let isStruct (t: FSharpType) =
                    let t = if t.IsAbbreviation then t.StripAbbreviations() else t
                    t.IsStructTupleType || (t.HasTypeDefinition && t.TypeDefinition.IsValueType)
                // `suffix` is how it compiled: "" or, for a generic reference-type result, " :> obj".
                let evaluated =
                    match eval (thunk "let mutable" "") with
                    | Choice2Of2 _, diags when diags |> Array.exists (fun d -> d.ErrorNumber = 30) ->
                        match resultType with
                        | Some t when isStruct t -> None
                        | _ -> Some(eval (thunk "let mutable" " :> obj"), " :> obj")
                    | r -> Some(r, "")
                match evaluated with
                | None ->
                    fail "measure needs a concrete result type here; add a type annotation, e.g. (Ok 5 : Result<int, string>)"
                | Some evaluated ->
                match evaluated with
                | (Choice2Of2 e, diags), _ ->
                    let shown = diagnostics diags
                    fail (if shown <> "" then shown else formatException e)
                | (Choice1Of2 _, _), suffix ->
                // A second, separately compiled copy to warm up first (see Bench.Run).
                let copyName = name + "_copy"
                match eval (thunkNamed "let mutable" copyName suffix) with
                | Choice2Of2 e, diags ->
                    let shown = diagnostics diags
                    fail (if shown <> "" then shown else formatException e)
                | Choice1Of2 _, _ ->
                let rec thunkType (t: Type) =
                    if isNull t then None
                    elif t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<unit -> unit> then Some t
                    else thunkType t.BaseType
                let bound = fsi.GetBoundValues()
                match bound |> List.tryFind (fun b -> b.Name = name), bound |> List.tryFind (fun b -> b.Name = copyName) with
                | Some b, Some copy ->
                    match thunkType b.Value.ReflectionType with
                    | None -> fail $"measure could not call the compiled expression (type {b.Value.ReflectionType})."
                    | Some t ->
                    let run =
                        typeof<Bench>.GetMethod("Run", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
                            .MakeGenericMethod(t.GetGenericArguments()[1])
                    let m =
                        onBigStack (fun () ->
                            run.Invoke(null, [| copy.Value.ReflectionValue; b.Value.ReflectionValue; box iterations |]) :?> Measurement)
                    let text =
                        $"{formatNs m.MedianNs} per call · {formatBytesPerCall m.BytesPerCall} B per call · GCs gen0 {m.Gen0} / gen1 {m.Gen1} / gen2 {m.Gen2} over {m.Calls} calls"
                    text, false, Some m
                | _ -> fail "measure could not get the compiled expression from the session."
            with e -> formatException e, true, None
        // Whatever the expression printed is dropped; output from before this call waits for the next eval.
        outCap.Drain() |> ignore
        errCap.Drain() |> ignore
        outCap.Write pendingOut
        errCap.Write pendingErr
        outcome

    /// What the session has bound so far: name and type.
    member _.Bindings() : string * bool =
        let ctx = FSharpDisplayContext.Empty.WithShortTypeNames true
        match fsi.GetBoundValues() |> List.filter (fun b -> not (b.Name.StartsWith measurePrefix)) with
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

/// Worker entry point. Requests: {op: init, refs, native, prelude} | {op: eval, code} | {op: typecheck, code}
/// | {op: bindings} | {op: measure, expr, iterations}; every reply is {text, isError}, some with extra fields.
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
            | "measure", Some s ->
                match s.Measure(str r "expr", (match r.TryGetProperty "iterations" with | true, v -> v.GetInt32() | _ -> 0)) with
                | text, isError, Some m ->
                    replyWith (text, isError) [
                        "measure",
                        box
                            {| medianNs = m.MedianNs
                               p90Ns = m.P90Ns
                               bytesPerCall = m.BytesPerCall
                               gen0 = m.Gen0
                               gen1 = m.Gen1
                               gen2 = m.Gen2
                               iterations = m.Calls
                               gcServer = Runtime.GCSettings.IsServerGC |} ]
                | text, isError, None -> reply (text, isError)
            | op, _ -> reply ($"worker: bad op '{op}' (or no session)", true)
        with e -> reply ($"worker error: {formatException e}", true)
        line <- protocolIn.ReadLine()
    0
