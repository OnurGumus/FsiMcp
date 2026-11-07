/// Type-checks a project with FCS in the server process, optionally with some files' contents replaced
/// (overlays), without building it and without touching the F# Interactive session.
module FsiMcp.ProjectChecker

#nowarn "57" // DocumentSource is marked experimental

open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
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

/// Design-time targets that fill FscCommandLineArgs without running the compiler. NonExistentFile makes
/// CoreCompile run even when the project is up to date; ArtifactsPath keeps obj/ out of the project.
let private commandLineArgs (projectFile: string) (configuration: string) (artifactsPath: string option) =
    let dir = Path.GetDirectoryName projectFile
    let args =
        [ "msbuild"; projectFile; "-restore"
          "-t:ResolveAssemblyReferencesDesignTime;ResolveProjectReferencesDesignTime;ResolvePackageDependenciesDesignTime;FindReferenceAssembliesForReferences;CoreCompile"
          $"-p:Configuration={configuration}"; "-p:ProvideCommandLineArgs=true"; "-p:SkipCompilerExecution=true"
          "-p:DesignTimeBuild=true"; "-p:NonExistentFile=__fsimcp_never__" ]
        @ (artifactsPath |> Option.map (fun p -> $"-p:ArtifactsPath={p}") |> Option.toList)
        @ [ "-nologo"; "-getItem:FscCommandLineArgs" ]
    let code, out, err = runProcess "dotnet" args dir
    let jsonStart = out.IndexOf('{')
    if code <> 0 || jsonStart < 0 then
        Error(clip 6000 $"Could not get the compiler arguments (dotnet msbuild exit {code}).\n{msbuildErrors out err}")
    else
        use doc = JsonDocument.Parse(out.Substring jsonStart)
        match doc.RootElement.GetProperty("Items").TryGetProperty "FscCommandLineArgs" with
        | true, items ->
            items.EnumerateArray()
            |> Seq.map (fun i -> str i "Identity")
            // source files come relative to the project folder
            |> Seq.map (fun a -> if a.StartsWith "-" then a else Path.GetFullPath(a, dir))
            |> Array.ofSeq
            |> Ok
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
                    })
        )

    let argsCache = Dictionary<string * string * string option, DateTime list * string[]>()

    /// Per project-options key: the version of each source file the checker last saw. With a custom
    /// document source FCS learns of changed contents only through NotifyFileChanged.
    let seen = Dictionary<string, Dictionary<string, string>>()

    let getArgs (projectFile: string) configuration artifactsPath (refresh: bool) =
        let key = projectFile, configuration, artifactsPath
        let stamp = argsStamp projectFile
        match argsCache.TryGetValue key with
        | true, (s, args) when not refresh && s = stamp -> Ok args
        | _ ->
            commandLineArgs projectFile configuration artifactsPath
            |> Result.map (fun args ->
                argsCache[key] <- (stamp, args)
                args)

    member _.Check(projectFile: string, configuration: string, artifactsPath: string option, overlayList: (string * string) list) : Result<Diagnostic list, string> =
        if overlayList |> List.exists (fun (p, _) -> String.IsNullOrWhiteSpace p) then Error "Every overlay needs a path." else
        let dir = Path.GetDirectoryName projectFile
        let ov = Dictionary<string, string>(pathComparer)
        let order = ResizeArray<string>()
        for p, content in overlayList do
            let full = Path.GetFullPath(p, dir)
            if not (ov.ContainsKey full) then order.Add full
            ov[full] <- content
        let missingRef (args: string[]) =
            args |> Array.exists (fun a -> a.StartsWith "-r:" && not (File.Exists(a.Substring 3)))
        let args =
            getArgs projectFile configuration artifactsPath false
            |> Result.bind (fun args ->
                if not (missingRef args) then Ok args
                else
                    // A referenced project was never built: build once, then ask again.
                    match resolveProject projectFile true configuration artifactsPath with
                    | Error e -> Error $"A referenced assembly is missing and building the project failed:\n{e}"
                    | Ok _ -> getArgs projectFile configuration artifactsPath true)
        match args with
        | Error e -> Error e
        | Ok args ->
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
                | true, old when old <> version -> checker.NotifyFileChanged(f, opts) |> Async.RunSynchronously
                | _ -> ()
                versions[f] <- version
            overlays <- ov
            try
                let results = checker.ParseAndCheckProject(opts) |> Async.RunSynchronously
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
