/// CPU and allocation profiles of one eval. The server records the WORKER over EventPipe, so the
/// diagnostics libraries never load into the process that hosts the user's project.
module FsiMcp.Profiler

open System
open System.Collections.Generic
open System.Diagnostics.Tracing
open System.IO
open System.Threading.Tasks
open Microsoft.Diagnostics.NETCore.Client
open Microsoft.Diagnostics.Tracing
open Microsoft.Diagnostics.Tracing.Etlx
open Microsoft.Diagnostics.Tracing.EventPipe
open Microsoft.Diagnostics.Tracing.Parsers.Clr
open FsiMcp.Text

type Mode =
    | Cpu
    | Alloc

// Microsoft-Windows-DotNETRuntime keywords.
let private gcKeyword = 0x1L
let private loaderKeyword = 0x8L
let private jitKeyword = 0x10L

/// Only the sample profiler (+ JIT/loader so stacks resolve): the default dotnet-trace CPU profile adds
/// providers that inflated one real 164 s run past 11 minutes.
let private providers mode =
    match mode with
    | Cpu ->
        [ EventPipeProvider("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational)
          EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Informational, jitKeyword ||| loaderKeyword) ]
    | Alloc ->
        // GC at Verbose = an AllocationTick (with stack) about every 100 KB allocated.
        [ EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, gcKeyword ||| jitKeyword ||| loaderKeyword) ]

/// Run `action` while an EventPipe session records process `pid`; returns its result and the trace file.
let record (pid: int) (mode: Mode) (action: unit -> 'T) : 'T * string =
    let dir = Path.Combine(Path.GetTempPath(), "fsi-mcp")
    Directory.CreateDirectory dir |> ignore
    let path = Path.Combine(dir, $"profile-{Environment.ProcessId}-{Guid.NewGuid():N}.nettrace")
    let session = DiagnosticsClient(pid).StartEventPipeSession(providers mode, true, 512)
    let copy =
        Task.Run(
            Action(fun () ->
                use fs = File.Create path
                session.EventStream.CopyTo fs)
        )
    try
        action (), path
    finally
        try session.Stop() with _ -> ()
        try copy.Wait(TimeSpan.FromSeconds 60.) |> ignore with _ -> ()
        session.Dispose()

/// Frames from these modules are runtime/library code, not the user's.
let private isUserModule (m: string) =
    not (String.IsNullOrEmpty m)
    && not (m = "System" || m = "netstandard" || m = "mscorlib")
    && not ([ "System."; "Microsoft."; "FSharp.Core"; "FSharp.Compiler"; "FSharp.DependencyManager"; "FsiMcp" ] |> List.exists m.StartsWith)

let private moduleOf (f: TraceCallStack) =
    match f.CodeAddress.ModuleFile with
    | null -> ""
    | m -> m.Name

let private shortName (m: string) =
    if String.IsNullOrEmpty m then "?"
    elif m.Contains "PollGC" then "(waiting for a garbage collection)"
    else
        let m = match m.IndexOf '(' with | p when p > 0 -> m.Substring(0, p) | _ -> m
        if m.Length > 120 then m.Substring(0, 120) + "…" else m

let private bump (d: Dictionary<string, float>) (k: string) (w: float) =
    d[k] <- (match d.TryGetValue k with | true, v -> v | _ -> 0.0) + w

let private topLines (d: Dictionary<string, float>) (total: float) (n: int) =
    d
    |> Seq.sortByDescending (fun kv -> kv.Value)
    |> Seq.truncate n
    |> Seq.map (fun kv -> sprintf "%6.1f%%  %s" (100.0 * kv.Value / total) kv.Key)
    |> String.concat "\n"

let private cpuReport (log: TraceLog) (top: int) =
    let self = Dictionary<string, float>()
    let total = Dictionary<string, float>()
    let mutable n = 0.0
    for ev in log.Events do
        match ev with
        | :? ClrThreadSampleTraceData as s when s.Type = ClrThreadSampleType.Managed ->
            match ev.CallStack() with
            | null -> ()
            | cs ->
                n <- n + 1.0
                bump self (shortName cs.CodeAddress.FullMethodName) 1.0
                let seen = HashSet<string>()
                let mutable f = cs
                while not (isNull f) do
                    if isUserModule (moduleOf f) then
                        let name = shortName f.CodeAddress.FullMethodName
                        if seen.Add name then bump total name 1.0
                    f <- f.Caller
        | _ -> ()
    if n = 0.0 then "No CPU samples: the eval was too short to sample (one sample per thread per ~1 ms)."
    else
        $"CPU: {n:F0} samples of threads running managed code (blocked and idle threads excluded).\n\n"
        + $"Top by self time:\n{topLines self n top}\n\n"
        + $"Your code by total time (self + callees):\n{topLines total n top}"

let private allocReport (log: TraceLog) (top: int) =
    let byType = Dictionary<string, float>()
    let bySite = Dictionary<string, float>()
    let mutable bytes = 0.0
    for ev in log.Events do
        match ev with
        | :? GCAllocationTickTraceData as a ->
            let b = float a.AllocationAmount64
            bytes <- bytes + b
            bump byType a.TypeName b
            // the nearest frame in the user's code is where the allocation was asked for
            let mutable f = ev.CallStack()
            let mutable site = "?"
            while not (isNull f) && site = "?" do
                if isUserModule (moduleOf f) then site <- shortName f.CodeAddress.FullMethodName
                f <- f.Caller
            bump bySite $"{a.TypeName}  <=  {site}" b
        | _ -> ()
    if bytes = 0.0 then "No allocation samples (one is taken about every 100 KB allocated)."
    else
        $"Allocations: ~{formatBytes bytes} sampled (about one sample per 100 KB).\n\n"
        + $"By type:\n{topLines byType bytes top}\n\n"
        + $"By type, at the nearest frame in your code:\n{topLines bySite bytes top}"

/// Summarise a recorded trace, then delete it.
let analyze (mode: Mode) (path: string) (top: int) : string =
    let etlx = TraceLog.CreateFromEventPipeDataFile path
    try
        use log = TraceLog.OpenOrConvert etlx
        match mode with
        | Cpu -> cpuReport log top
        | Alloc -> allocReport log top
    finally
        for f in [ etlx; path ] do
            try File.Delete f with _ -> ()
