/// Turns a project file into the list of assemblies an F# Interactive session must reference to run its code.
module FsiMcp.ProjectResolver

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Reflection.Metadata
open System.Reflection.PortableExecutable
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open FsiMcp.Text

/// RIDs this machine can run, most specific first (e.g. osx-arm64; osx; unix).
let ridChain =
    let os =
        if OperatingSystem.IsWindows() then "win"
        elif OperatingSystem.IsMacOS() then "osx"
        else "linux"
    let arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
    [ $"{os}-{arch}"; os ] @ (if OperatingSystem.IsWindows() then [] else [ "unix" ])

/// Rank of a package-relative asset path for this machine (lower = better), None = another platform's asset.
/// A portable app's ReferenceCopyLocalPaths lists lib/ AND every runtimes/<rid>/ variant; deps.json normally picks.
let ridRank (pathInPackage: string) =
    let p = pathInPackage.Replace('\\', '/')
    if p.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase) then
        let rid = p.Split('/').[1]
        ridChain |> List.tryFindIndex (fun r -> String.Equals(r, rid, StringComparison.OrdinalIgnoreCase))
    else Some ridChain.Length

/// Run a process to completion; when `ct` fires, its whole process tree is killed and
/// OperationCanceledException is raised.
let runProcessCancellable (file: string) (args: string list) (cwd: string) (ct: CancellationToken) =
    let psi = ProcessStartInfo(file)
    psi.WorkingDirectory <- cwd
    psi.UseShellExecute <- false
    psi.RedirectStandardInput <- true // never let a child read the JSON-RPC stream
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.Environment["MSBUILDTERMINALLOGGER"] <- "off"
    psi.Environment["DOTNET_CLI_UI_LANGUAGE"] <- "en"
    // With this set (`dotnet test` sets it, so a server started under a test run inherits it), MSBuild
    // starts its worker nodes on its own stdout/stderr. A node outlives the run (node reuse), holding
    // the pipes open, so reading them to the end would wait until the idle node exits.
    psi.Environment.Remove "MSBUILDENSURESTDOUTFORTASKPROCESSES" |> ignore
    for a in args do psi.ArgumentList.Add a
    use p = Process.Start psi
    p.StandardInput.Close()
    let out = p.StandardOutput.ReadToEndAsync()
    let err = p.StandardError.ReadToEndAsync()
    (
        use _ = ct.Register(fun () -> try p.Kill(true) with _ -> ())
        p.WaitForExit()
    )
    ct.ThrowIfCancellationRequested()
    p.ExitCode, out.Result, err.Result

let runProcess (file: string) (args: string list) (cwd: string) = runProcessCancellable file args cwd CancellationToken.None

/// The error lines of a failed MSBuild run (else its first 40 lines). On failure MSBuild still prints the
/// item JSON (hundreds of KB) before the errors: keep the errors.
let msbuildErrors (out: string) (err: string) =
    let lines = (out + "\n" + err).Split('\n') |> Array.map (fun l -> l.TrimEnd())
    let errors =
        lines |> Array.filter (fun l -> l.Contains(": error ") || l.Contains("error MSB") || l.Contains("error NU"))
    if errors.Length > 0 then errors |> Array.distinct |> String.concat "\n"
    else String.Join("\n", lines |> Array.filter ((<>) "") |> Array.truncate 40)

type Resolved =
    { ProjectFile: string
      TargetPath: string
      /// Assemblies to reference (-r:), in order: the project's output, its closure, host copies, ASP.NET Core.
      Managed: string list
      /// Managed for an editor: the project's own copy where the session runs the host's (the host's copy
      /// lives in this tool's install folder, which a tool update deletes).
      EditorRefs: string list
      /// Of Managed: files the project's own build produces (they change on rebuild; NuGet-cache files never do).
      Built: Set<string>
      /// Native library file name -> path, best match for this machine.
      Native: (string * string) list
      Notes: string list }

/// Assemblies the host process already has (BCL, FSharp.Core, FCS). They win in the default load context.
let hostAssemblies =
    let d = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    for p in (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") :?> string).Split(Path.PathSeparator) do
        d.TryAdd(Path.GetFileNameWithoutExtension p, p) |> ignore
    d

let findProjectFile (path: string) =
    let full = Path.GetFullPath path
    let isProject (f: string) =
        [ ".fsproj"; ".csproj"; ".vbproj" ] |> List.exists (fun e -> f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
    if File.Exists full then Ok full
    elif Directory.Exists full then
        match Directory.GetFiles(full) |> Array.filter isProject with
        | [| one |] -> Ok one
        | [||] -> Error $"No project file in {full}"
        | many ->
            let names = String.Join(", ", many |> Array.map Path.GetFileName)
            Error $"Several project files in {full}; pass one of: {names}"
    else Error $"Not found: {full}"

/// Item types that together make up the runtime closure:
/// - ReferenceCopyLocalPaths: what an APP copies to bin (project refs + NuGet). Empty of NuGet for a library.
/// - Runtime/RuntimeTargets/NativeCopyLocalItems: NuGet's runtime assets, computed for libraries too.
let private closureItems =
    [ "ReferenceCopyLocalPaths"; "RuntimeCopyLocalItems"; "RuntimeTargetsCopyLocalItems"; "NativeCopyLocalItems" ]

/// Ask MSBuild for the project's runtime closure instead of rewriting reference-assembly paths.
/// NB: -getItem without a target only EVALUATES; these items are filled by targets and come back [].
/// `failNote` is appended to the first line of a failure message; `ct` kills the MSBuild run.
let resolveProjectWith (ct: CancellationToken) (failNote: string) (projectFile: string) (build: bool) (configuration: string) (artifactsPath: string option) : Result<Resolved, string> =
    let target = if build then "Build" else "ResolveReferences"
    let args =
        [ "msbuild"; projectFile; "-restore"; $"-t:{target}"; $"-p:Configuration={configuration}"; "-nologo"
          "-getProperty:TargetPath"; "-getItem:ReferencePath" ]
        @ (closureItems |> List.map (fun i -> "-getItem:" + i))
        @ (artifactsPath |> Option.map (fun p -> $"-p:ArtifactsPath={p}") |> Option.toList)
    let code, out, err = runProcessCancellable "dotnet" args (Path.GetDirectoryName projectFile) ct
    let jsonStart = out.IndexOf('{')
    if code <> 0 || jsonStart < 0 then
        Error(clip 6000 $"Build/resolve failed (dotnet msbuild exit {code}){failNote}.\n{msbuildErrors out err}")
    else
    use doc = JsonDocument.Parse(out.Substring jsonStart)
    let root = doc.RootElement
    let targetPath = root.GetProperty("Properties").GetProperty("TargetPath").GetString()
    let items (name: string) =
        match root.GetProperty("Items").TryGetProperty name with
        | true, v -> v.EnumerateArray() |> Seq.toList
        | _ -> []
    let refPaths = items "ReferencePath"
    // Copy-local entries first: on equal rank the first wins, and a ReferencePath entry for a project
    // reference can be a metadata-only reference assembly (obj/ref/).
    let copyLocal =
        (closureItems |> List.collect items)
        @ (refPaths |> List.filter (fun i -> str i "ReferenceSourceTarget" = "ProjectReference"))
        |> List.distinctBy (fun i -> str i "Identity")
    let subPath (i: JsonElement) =
        match str i "PathInPackage" with
        | "" -> str i "DestinationSubPath"
        | p -> p
    let best (cands: JsonElement list) keyOf =
        cands
        |> List.choose (fun i -> ridRank (subPath i) |> Option.map (fun r -> r, i))
        |> List.groupBy (fun (_, i) -> keyOf (str i "Identity"))
        |> List.map (fun (k, c) -> k, c |> List.minBy fst |> snd)
    let notes = ResizeArray<string>()

    let managedItems =
        best
            (copyLocal
             |> List.filter (fun i -> str i "Extension" = ".dll" && (let t = str i "AssetType" in t = "" || t = "runtime")))
            (fun p -> Path.GetFileNameWithoutExtension(p).ToLowerInvariant())
        |> List.map snd
    let managed = managedItems |> List.map (fun i -> str i "Identity")
    let keep, shadowed =
        managed |> List.partition (fun p -> not (hostAssemblies.ContainsKey(Path.GetFileNameWithoutExtension p)))
    for p in shadowed do
        try
            let name = Path.GetFileNameWithoutExtension p
            let want = AssemblyName.GetAssemblyName(p).Version
            let have = AssemblyName.GetAssemblyName(hostAssemblies[name]).Version
            if want > have then
                notes.Add $"CONFLICT: project wants {name} {want}, host has {have} (host wins; may fail at runtime)"
        with _ -> ()
    // The host's copy is what will run, so compile against it too. Shared-framework assemblies, System.*
    // and FSharp.Core are already FSI's default references; app-local ones (e.g. FCS itself) are not.
    let frameworkDir = Path.TrimEndingDirectorySeparator(RuntimeEnvironment.GetRuntimeDirectory())
    // (the project's copy, the host's copy) of each app-local assembly the host shadows.
    let hostShadowed =
        shadowed
        |> List.map (fun p -> p, hostAssemblies[Path.GetFileNameWithoutExtension p])
        |> List.filter (fun (_, h) ->
            let name = Path.GetFileName h
            not (h.StartsWith frameworkDir)
            && not (name.Equals("FSharp.Core.dll", StringComparison.OrdinalIgnoreCase))
            && not (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)))
    let hostCopies = hostShadowed |> List.map snd

    let frameworks =
        refPaths |> List.map (fun i -> str i "FrameworkReferenceName") |> List.filter ((<>) "") |> List.distinct
    let aspnet =
        if not (List.contains "Microsoft.AspNetCore.App" frameworks) then []
        else
            let aspRoot = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName frameworkDir), "Microsoft.AspNetCore.App")
            let v = Environment.Version
            let dir =
                if not (Directory.Exists aspRoot) then None
                else
                    Directory.GetDirectories aspRoot
                    |> Array.choose (fun d ->
                        match Version.TryParse(Path.GetFileName d) with
                        | true, dv when dv.Major = v.Major && dv.Minor = v.Minor -> Some(dv, d)
                        | _ -> None)
                    |> Array.sortByDescending fst
                    |> Array.tryHead
            match dir with
            | None ->
                notes.Add $"ASP.NET Core shared framework {v.Major}.{v.Minor} not found under {aspRoot}"
                []
            | Some(dv, d) ->
                notes.Add $"ASP.NET Core shared framework {dv}"
                refPaths
                |> List.filter (fun i -> str i "FrameworkReferenceName" = "Microsoft.AspNetCore.App")
                |> List.map (fun i -> Path.Combine(d, Path.GetFileName(str i "Identity")))
                |> List.filter File.Exists
    for f in frameworks do
        if f <> "Microsoft.NETCore.App" && f <> "Microsoft.AspNetCore.App" then
            notes.Add $"Framework {f} is not supported by this host (skipped)"

    let native =
        best (copyLocal |> List.filter (fun i -> str i "AssetType" = "native")) Path.GetFileName
        |> List.map (fun (name, i) -> name, str i "Identity")

    let own =
        if File.Exists targetPath then [ targetPath ]
        else
            notes.Add $"Output {targetPath} does not exist (build it)"
            []
    let builtRefs =
        managedItems |> List.filter (fun i -> str i "NuGetPackageId" = "") |> List.map (fun i -> str i "Identity")
    Ok
        { ProjectFile = projectFile
          TargetPath = targetPath
          Managed = own @ keep @ hostCopies @ aspnet
          EditorRefs = own @ keep @ (hostShadowed |> List.map fst) @ aspnet
          Built = set (own @ builtRefs) |> Set.filter (fun p -> List.contains p keep || List.contains p own)
          Native = native
          Notes = List.ofSeq notes }

/// load_project's resolve: never cancelled, and a failure leaves the previous session running.
let resolveProject (projectFile: string) (build: bool) (configuration: string) (artifactsPath: string option) =
    resolveProjectWith CancellationToken.None "; the previous session is still running" projectFile build configuration artifactsPath

/// Public top-level types of an assembly (namespaces + F# modules), read from metadata without loading it.
let publicSurface (path: string) =
    try
        use fs = File.OpenRead path
        use pe = new PEReader(fs)
        let md = pe.GetMetadataReader()
        md.TypeDefinitions
        |> Seq.map md.GetTypeDefinition
        |> Seq.filter (fun t ->
            t.GetDeclaringType().IsNil && (t.Attributes &&& TypeAttributes.VisibilityMask) = TypeAttributes.Public)
        |> Seq.map (fun t ->
            let ns = md.GetString t.Namespace
            let n = md.GetString(t.Name).Split('`').[0]
            if ns = "" then n else ns + "." + n)
        |> Seq.filter (fun n -> not (n.StartsWith "<"))
        |> Seq.distinct
        |> Seq.sort
        |> List.ofSeq
    with e -> [ $"(could not read metadata: {e.Message})" ]

/// Copies of the project's own build outputs, so a session never holds files a rebuild writes to
/// (Windows locks loaded dlls; on Unix the build truncates a file the worker has mapped).
module Snapshot =
    let root = Path.Combine(Path.GetTempPath(), "fsi-mcp")
    let mutable private counter = 0

    /// Copy `files` (+ their .pdb) into a fresh directory; returns it and a path -> copy mapping.
    let create (files: Set<string>) =
        counter <- counter + 1
        let dir = Path.Combine(root, $"{Environment.ProcessId}-{counter}")
        Directory.CreateDirectory dir |> ignore
        let copies =
            [ for f in files do
                let dest = Path.Combine(dir, Path.GetFileName f)
                File.Copy(f, dest, true)
                let pdb = Path.ChangeExtension(f, ".pdb")
                if File.Exists pdb then File.Copy(pdb, Path.ChangeExtension(dest, ".pdb"), true)
                yield f, dest ]
            |> Map.ofList
        dir, copies

    let delete (dir: string) =
        try
            if Directory.Exists dir then Directory.Delete(dir, true)
        with _ -> ()

    /// Remove snapshots left behind by server processes that no longer exist.
    let sweepStale () =
        try
            if Directory.Exists root then
                for d in Directory.GetDirectories root do
                    match Int32.TryParse((Path.GetFileName d).Split('-').[0]) with
                    | true, pid when pid <> Environment.ProcessId ->
                        let alive =
                            try
                                use p = Process.GetProcessById pid
                                not p.HasExited
                            with _ -> false
                        if not alive then delete d
                    | _ -> ()
        with _ -> ()
