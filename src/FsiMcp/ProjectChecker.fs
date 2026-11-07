/// Type-checks a project with FCS in the server process, optionally with some files' contents replaced
/// (overlays), without touching the F# Interactive session. It builds only when a referenced project's
/// output is missing or older than its sources.
module FsiMcp.ProjectChecker

#nowarn "57" // DocumentSource is marked experimental

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Threading
open FSharp.Compiler.CodeAnalysis
open FSharp.Compiler.Diagnostics
open FSharp.Compiler.Text
open FsiMcp.Text
open FsiMcp.ProjectResolver

type Diagnostic =
    { [<JsonPropertyName "path">] Path: string
      [<JsonPropertyName "line">] Line: int
      [<JsonPropertyName "column">] Column: int
      /// "error" | "warning" | "info"
      [<JsonPropertyName "severity">] Severity: string
      /// e.g. "FS0001"
      [<JsonPropertyName "code">] Code: string
      [<JsonPropertyName "message">] Message: string }

/// How file paths compare on this OS's usual file system.
let private pathComparer =
    if OperatingSystem.IsLinux() then StringComparer.Ordinal else StringComparer.OrdinalIgnoreCase

/// A project the checked project references, and the assembly its build produces.
type private ProjectRef = { ProjectFile: string; Assembly: string }

/// What the design-time run returns: the compiler arguments, and the referenced projects.
type private Args = { CommandLine: string[]; ProjectRefs: ProjectRef list }

/// Design-time targets that fill FscCommandLineArgs without running the compiler. NonExistentFile makes
/// CoreCompile run even when the project is up to date; ArtifactsPath keeps obj/ out of the project.
/// ReferencePath items from project references carry the referenced project file; their Identity is the
/// implementation assembly (the -r: argument is often the reference assembly, which a build rewrites only
/// when the public surface changes, so its time says nothing about whether the build is current).
let private commandLineArgs (projectFile: string) (configuration: string) (artifactsPath: string option) (ct: CancellationToken) =
    let dir = Path.GetDirectoryName projectFile
    let args =
        [ "msbuild"; projectFile; "-restore"
          "-t:ResolveAssemblyReferencesDesignTime;ResolveProjectReferencesDesignTime;ResolvePackageDependenciesDesignTime;FindReferenceAssembliesForReferences;CoreCompile"
          $"-p:Configuration={configuration}"; "-p:ProvideCommandLineArgs=true"; "-p:SkipCompilerExecution=true"
          "-p:DesignTimeBuild=true"; "-p:NonExistentFile=__fsimcp_never__" ]
        @ (artifactsPath |> Option.map (fun p -> $"-p:ArtifactsPath={p}") |> Option.toList)
        @ [ "-nologo"; "-getItem:FscCommandLineArgs"; "-getItem:ReferencePath" ]
    let code, out, err = runProcessCancellable "dotnet" args dir ct
    let jsonStart = out.IndexOf('{')
    if code <> 0 || jsonStart < 0 then
        Error(clip 6000 $"Could not get the compiler arguments (dotnet msbuild exit {code}).\n{msbuildErrors out err}")
    else
        use doc = JsonDocument.Parse(out.Substring jsonStart)
        let items = doc.RootElement.GetProperty("Items")
        match items.TryGetProperty "FscCommandLineArgs" with
        | true, fsc ->
            let commandLine =
                fsc.EnumerateArray()
                |> Seq.map (fun i -> str i "Identity")
                // source files come relative to the project folder
                |> Seq.map (fun a -> if a.StartsWith "-" then a else Path.GetFullPath(a, dir))
                |> Array.ofSeq
            let projectRefs =
                match items.TryGetProperty "ReferencePath" with
                | true, refs ->
                    [ for i in refs.EnumerateArray() do
                        let proj = str i "MSBuildSourceProjectFile"
                        if str i "ReferenceSourceTarget" = "ProjectReference" && proj <> "" then
                            yield { ProjectFile = Path.GetFullPath proj; Assembly = str i "Identity" } ]
                    |> List.distinct
                | _ -> []
            Ok { CommandLine = commandLine; ProjectRefs = projectRefs }
        | _ -> Error "dotnet msbuild returned no compiler arguments (FscCommandLineArgs)."

/// Last-write times of the files whose change invalidates the arguments: the project file, and any
/// Directory.Build.props/.targets or Directory.Packages.props in its folder or above (missing ones too,
/// so adding one counts as a change).
let private argsStamp (projectFile: string) =
    let names = [ "Directory.Build.props"; "Directory.Build.targets"; "Directory.Packages.props" ]
    let rec up (d: DirectoryInfo) = if isNull d then [] else d.FullName :: up d.Parent
    projectFile :: [ for d in up (DirectoryInfo(Path.GetDirectoryName projectFile)) do for n in names -> Path.Combine(d, n) ]
    |> List.map File.GetLastWriteTimeUtc

let private hash (s: string) = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes s))

/// The newest last-write time of the F# sources and project files under a project's folder, skipping
/// bin/, obj/, hidden folders, the artifacts path and folders of other projects nested in it.
let private newestSource (projectFile: string) (artifactsPath: string option) =
    let isProject (f: string) =
        [ ".fsproj"; ".csproj"; ".vbproj" ] |> List.exists (fun e -> f.EndsWith(e, StringComparison.OrdinalIgnoreCase))
    let skip (d: string) =
        let name = Path.GetFileName d
        name = "bin" || name = "obj" || name.StartsWith "."
        || (artifactsPath |> Option.exists (fun a -> pathComparer.Equals(Path.TrimEndingDirectorySeparator a, d)))
        || Directory.EnumerateFiles d |> Seq.exists isProject
    let rec walk (d: string) =
        seq {
            for f in Directory.EnumerateFiles d do
                match Path.GetExtension f with
                | ".fs" | ".fsi" | ".fsproj" -> yield File.GetLastWriteTimeUtc f
                | _ -> ()
            for sub in Directory.EnumerateDirectories d do
                if not (skip sub) then yield! walk sub
        }
    try walk (Path.GetDirectoryName projectFile) |> Seq.fold max DateTime.MinValue
    with _ -> DateTime.MinValue

/// One FSharpChecker for the life of the server. Calls must not overlap (the server serializes tool calls).
type Checker() =
    /// Overlays for the duration of one Check; every other file is read from disk.
    let mutable overlays = Dictionary<string, string>(pathComparer)

    let checker =
        FSharpChecker.Create(
            documentSource =
                DocumentSource.Custom(fun path ->
                    async {
                        match overlays.TryGetValue path with
                        | true, content -> return Some(SourceText.ofString content)
                        | _ when File.Exists path -> return Some(SourceText.ofString (File.ReadAllText path))
                        | _ -> return None
                    }),
            keepAllBackgroundResolutions = false,
            keepAllBackgroundSymbolUses = false
        )

    let argsCache = Dictionary<string * string * string option, DateTime list * Args>()

    /// Per referenced project: the newest source time a rebuild was last tried for. A source that a build
    /// does not compile (a stray .fs, or a reference that fails to build) then costs one build, not one
    /// per check.
    let rebuiltFor = Dictionary<string, DateTime>(pathComparer)

    /// Per project-options key: the version of each source file the checker last saw. With a custom
    /// document source FCS learns of changed contents only through NotifyFileChanged.
    let seen = Dictionary<string, Dictionary<string, string>>()

    let getArgs (projectFile: string) configuration artifactsPath (refresh: bool) (ct: CancellationToken) =
        let key = projectFile, configuration, artifactsPath
        let stamp = argsStamp projectFile
        match argsCache.TryGetValue key with
        | true, (s, args) when not refresh && s = stamp -> Ok args
        | _ ->
            commandLineArgs projectFile configuration artifactsPath ct
            |> Result.map (fun args ->
                argsCache[key] <- (stamp, args)
                args)

    /// Raises OperationCanceledException when `ct` fires (any MSBuild run it started is killed).
    member _.Check(projectFile: string, configuration: string, artifactsPath: string option, overlayList: (string * string) list, ct: CancellationToken) : Result<Diagnostic list, string> =
        if overlayList |> List.exists (fun (p, _) -> String.IsNullOrWhiteSpace p) then Error "Every overlay needs a path." else
        let dir = Path.GetDirectoryName projectFile
        let ov = Dictionary<string, string>(pathComparer)
        let order = ResizeArray<string>()
        for p, content in overlayList do
            let full = Path.GetFullPath(p, dir)
            if not (ov.ContainsKey full) then order.Add full
            ov[full] <- content
        let missingRefs (args: Args) =
            args.CommandLine
            |> Array.filter (fun a -> a.StartsWith "-r:" && not (File.Exists(a.Substring 3)))
            |> Array.map (fun a -> a.Substring 3)
        /// Referenced projects whose sources are newer than their assembly (not yet retried for that time).
        let staleRefs (args: Args) =
            [ for r in args.ProjectRefs do
                let newest = newestSource r.ProjectFile artifactsPath
                let built = if File.Exists r.Assembly then File.GetLastWriteTimeUtc r.Assembly else DateTime.MinValue
                let tried = match rebuiltFor.TryGetValue r.ProjectFile with | true, t -> t | _ -> DateTime.MinValue
                if newest > built && newest > tried then yield r.ProjectFile, newest ]
        let args =
            getArgs projectFile configuration artifactsPath false ct
            |> Result.bind (fun args ->
                let stale = staleRefs args
                if missingRefs args |> Array.isEmpty && List.isEmpty stale then Ok args
                else
                    // A referenced project was never built, or changed since: build once, then ask again.
                    // References build before the project, so a project whose own files fail to compile is
                    // still checkable.
                    let built = resolveProjectWith ct "" projectFile true configuration artifactsPath
                    for p, newest in stale do rebuiltFor[p] <- newest
                    getArgs projectFile configuration artifactsPath true ct
                    |> Result.bind (fun args ->
                        match missingRefs args with
                        | [||] -> Ok args
                        | missing ->
                            let why = match built with Error e -> $"\nThe build failed:\n{e}" | Ok _ -> ""
                            Error $"The check could not run: referenced assemblies are missing even after building the project:\n{String.Join('\n', missing)}{why}"))
        match args with
        | Error e -> Error e
        | Ok { CommandLine = args } ->
            // GetProjectOptionsFromCommandLineArgs leaves SourceFiles empty and keeps the source files in
            // OtherOptions (checked after SourceFiles), so split them off here: an added overlay goes last.
            let flags, sourceFiles = args |> Array.partition (fun a -> a.StartsWith "-")
            let opts = checker.GetProjectOptionsFromCommandLineArgs(projectFile, flags)
            let sources = HashSet<string>(sourceFiles, pathComparer)
            let added = order |> Seq.filter (fun f -> not (sources.Contains f)) |> Array.ofSeq
            let opts = { opts with SourceFiles = Array.append sourceFiles added }
            let key = String.Join("\n", Array.concat [ [| projectFile |]; opts.OtherOptions; opts.SourceFiles ])
            let versions =
                match seen.TryGetValue key with
                | true, v -> v
                | _ ->
                    let v = Dictionary<string, string>(pathComparer)
                    seen[key] <- v
                    v
            for f in opts.SourceFiles do
                let version =
                    match ov.TryGetValue f with
                    | true, content -> "overlay " + hash content
                    | _ ->
                        let fi = FileInfo f
                        if fi.Exists then $"disk {fi.LastWriteTimeUtc.Ticks} {fi.Length}" else "missing"
                match versions.TryGetValue f with
                | true, old when old <> version -> Async.RunSynchronously(checker.NotifyFileChanged(f, opts), cancellationToken = ct)
                | _ -> ()
                versions[f] <- version
            overlays <- ov
            try
                let results = Async.RunSynchronously(checker.ParseAndCheckProject(opts), cancellationToken = ct)
                let relative (f: string) =
                    if String.IsNullOrEmpty f || not (Path.IsPathRooted f) then f
                    else Path.GetRelativePath(dir, f).Replace('\\', '/')
                results.Diagnostics
                |> Array.choose (fun d ->
                    let severity =
                        match d.Severity with
                        | FSharpDiagnosticSeverity.Error -> Some "error"
                        | FSharpDiagnosticSeverity.Warning -> Some "warning"
                        | FSharpDiagnosticSeverity.Info -> Some "info"
                        | _ -> None // Hidden
                    severity
                    |> Option.map (fun s ->
                        { Path = relative d.FileName
                          Line = d.StartLine
                          Column = d.StartColumn + 1
                          Severity = s
                          Code = sprintf "FS%04d" d.ErrorNumber
                          Message = d.Message }))
                |> Array.distinct
                |> List.ofArray
                |> Ok
            finally
                overlays <- Dictionary<string, string>(pathComparer)
