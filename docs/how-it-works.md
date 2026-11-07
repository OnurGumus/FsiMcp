# How FsiMcp works

## Two processes

The MCP server owns a worker process (`fsi-mcp --worker`) that hosts `FsiEvaluationSession` from
FSharp.Compiler.Service. .NET cannot unload assemblies from the default load context or stop a runaway
thread, so an in-process host would keep running the *old* dll after a rebuild and could hang forever on
`while true do ()`. Replacing the worker solves both, in about a second.

## Loading a project

`load_project` asks MSBuild for the runtime closure instead of rewriting reference-assembly paths:

```
dotnet msbuild <project> -restore -t:Build -getProperty:TargetPath -getItem:ReferencePath
    -getItem:ReferenceCopyLocalPaths -getItem:RuntimeCopyLocalItems
    -getItem:RuntimeTargetsCopyLocalItems -getItem:NativeCopyLocalItems
```

This is where hand-written `references.fsx` files (pointing at metadata-only reference assemblies, with
hard-coded TFMs and machine paths) tend to break:

- `-getItem` without a target only *evaluates* the project; these items are filled by targets.
- A library's `ReferenceCopyLocalPaths` has no NuGet packages (libraries don't copy them to `bin`);
  `RuntimeCopyLocalItems` does.
- A portable app's closure lists `lib/` *and* every `runtimes/<rid>/lib/` variant of an assembly; the one for
  this machine's RID is chosen (normally `deps.json` does that at startup).
- Native libraries (e.g. `lmdb.dylib`, `e_sqlite3`) are served through `AssemblyLoadContext.ResolvingUnmanagedDll`.
- `FrameworkReference Microsoft.AspNetCore.App` maps to the installed ASP.NET Core shared framework.
- The project's own build outputs are copied to a temp folder before loading, so a session never holds files
  a rebuild writes to (Windows locks loaded dlls; on Unix a rebuild truncates mapped files).

A failed build returns only the compiler errors and keeps the current session running.

## Output for an LLM

- `let` bindings echo only their type (`val x: T`); evaluate an expression to see a value.
- Values print with limits (20 items, depth 6); long output keeps its head and tail.
- `printfn` from user code or background threads never reaches the JSON-RPC stream: it is captured per
  call, and output written between calls arrives with the next one.
- `eval` takes `timeout_seconds` (default 120). On timeout or stack overflow the worker is replaced and the
  agent is told that bindings were lost.
- `check_project` results also carry `structuredContent` for clients that read it: its `diagnostics` (path,
  line, column, severity, code, message). `load_project` adds `structuredContent` with its `references` and
  `targetPath` only when called with `return_references: true`; otherwise its result is text only, as in 0.2.0.
  `eval` does the same with `return_structured: true` (its outcome and cost: `ok`, `restarted`, `stats`;
  when the session was lost to a timeout or crash, `restarted` is true and `stats` are the wall time until
  then and zeros), and `load_project` with `return_references: true` also reports the prelude's outcome.
  `measure` returns `structuredContent` on success (see Measuring); its errors and refusals are text only,
  except that a `measure` which lost the session returns `{ "restarted": true }` with its error text.

## Session options

`load_project` takes options that stay in force for later loads, resets and timeout restarts until changed:

- `prelude`: an `.fsx` file `#load`ed into every new session, with its module and its top-level `open`s
  opened, so its helpers are usable unqualified. `""` removes it (also accepted by `reset`).
- `gc_server`: run the session under Server GC, as ASP.NET apps do. Allocation-heavy code can run about twice
  as fast under it, so time code under the GC it will really run with.
- `artifacts_path`: a directory for build output (`bin`, `obj`), so nothing is written under the project.
  A relative path resolves against the server's working folder. `""` clears it.
- `env`: environment variables for the session process.

## Checking a project

`check_project` type-checks a project without touching the F# Interactive session, and without building it
except when a referenced project needs it (below). It asks MSBuild for the compiler arguments with a design-time run (`ResolveAssemblyReferencesDesignTime`,
`ResolveProjectReferencesDesignTime`, `ResolvePackageDependenciesDesignTime`,
`FindReferenceAssembliesForReferences`, `CoreCompile` with `ProvideCommandLineArgs` and `SkipCompilerExecution`),
then type-checks the sources with `FSharpChecker` in the server process.

- `overlays` replace the contents of files for this check only, so an agent can test an edit before saving
  it. A path that is not one of the project's files is added, and checked last.
- The arguments are cached per project, configuration and artifacts path. The cache is refreshed when the
  project file or `Directory.Build.props`, `Directory.Build.targets` or `Directory.Packages.props` changes.
- A referenced project that has never been built, or that changed since it was last built, triggers one
  build first. The design-time run also returns `ReferencePath`; its project-reference items name the
  referenced project and its output assembly. A reference is stale when a `.fs`, `.fsi` or `.fsproj` file
  under its folder (skipping `bin/`, `obj/`, hidden folders, the artifacts path and nested projects' folders)
  is newer than that assembly. The build is the same one `load_project` runs; it is tried once per change,
  so a reference that fails to build is checked as last built rather than rebuilt on every check.
- Without `artifacts_path`, the one remembered from `load_project` is used.
- A client that gives up sends `notifications/cancelled`: the check stops (its MSBuild run is killed, or
  the type-check is cancelled) and frees the server for the next call.

## Profiling

`profile` evaluates code like `eval` (same session) while the server records the worker over EventPipe;
the diagnostics libraries live in the server process, never in the session that hosts your project.

- `mode: "cpu"` (default) samples every thread about once a millisecond and counts only threads running
  managed code, so idle pool threads do not drown the table. It reports methods by self time, and frames
  from your own assemblies by total time. "(waiting for a garbage collection)" is time threads spent stopped
  for the GC: a large share means allocations are the real cost.
- `mode: "alloc"` records the runtime's allocation samples (about one per 100 KB) and reports bytes by type,
  and by type at the nearest frame in your code, which is where to look.

Slow (>= 0.5 s) or allocation-heavy (>= 100 MB) evals also end with `[time · bytes allocated · GC counts]`.
Allocation volume is a much steadier A/B signal than time.

## Measuring

`measure` gives the time and the bytes allocated per call of an expression, for comparing small code
(a list against an array, one parser against another):

1. The expression is bound in the session as a thunk, `fun () -> (expr)`, under a hidden name
   (`__fsimcp_measure_<n>`, never listed by `bindings`), so it is compiled once and sees the session's
   bindings. A compile error is returned with positions in the expression itself, and nothing is kept.
   An expression whose value is a function is refused: apply it first. A generic result such as
   `failwith ".."` or `[]` is measured as `obj`; a generic struct such as `Ok 5` or `ValueNone` would be
   boxed on every call, so it is refused until it is given a type (`(Ok 5 : Result<int, string>)`).
2. Warm-up: four rounds of 30 calls and a 150 ms pause, so tiered compilation (tier 0, instrumented,
   tier 1 with dynamic PGO) can settle, then about 300 ms of calls. A second copy of the thunk, compiled
   separately, is warmed the same way and longer (1.3 s of calls) before that: the code the expression
   calls must settle before the thunk itself is compiled at tier 1, or the first measure of an expression
   in a session can read slower and allocate more than every later one (416 B against 0 B per call for
   `Array.init 100 id |> Array.sum`, whose array .NET 10 can put on the stack). A round that takes more
   than 200 ms (not counting the very first call, which pays for JIT and loading) ends that warm-up at
   once: at that cost per call the JIT matters little, and a few calls are enough. An expression of
   300 ms per call is warmed with two calls of each copy.
3. Batches take about 1 ms each, and a run has at least 10 of them.
4. Batches run until `iterations` calls (10 to 1,000,000; fewer, negative included, run 10), or by
   default for about 1 s (at least 10 calls). Each batch's time per call is recorded; the result is the median and the 90th percentile over
   batches.
5. Bytes per call are `GC.GetAllocatedBytesForCurrentThread` before and after the loop, divided by the
   calls; GC counts are for the whole loop. The results are kept (`GC.KeepAlive`), so the work is not
   optimised away.

The loop runs on one thread, so allocations made on other threads (`async`, `Task`, `Parallel`) are not
counted; the structured result says so in its `note`. Its `gcServer` says which GC the numbers were taken
under (see `gc_server`). Anything the expression prints is dropped.

The expression runs many more times than the reported call count: a warmed copy, two warm-ups and about
1 s of batches. Side effects (for example `ResizeArray.Add`) therefore happen far more often than that
count suggests. `timeout_seconds` (default 60) works as for `eval`. Each call should take well under a
second; with the default `iterations` the whole run is about 14 calls' worth for a slow expression, so
very slow expressions (or many iterations of a slow one) need a larger `timeout_seconds`.

## Client configuration

Install once with `dotnet tool install -g FsiMcp` (update with `dotnet tool update -g FsiMcp`), then:

**Claude Code**

```sh
claude mcp add --scope user fsi -- fsi-mcp
```

**VS Code** (`.vscode/mcp.json`)

```json
{ "servers": { "fsi": { "type": "stdio", "command": "fsi-mcp" } } }
```

**Claude Desktop, Cursor and other `mcpServers` clients**

```json
{ "mcpServers": { "fsi": { "command": "fsi-mcp" } } }
```

**Without installing:** use `dnx` with args `["FsiMcp", "--yes"]` instead of `fsi-mcp`. The first launch
downloads the package, which can take longer than some clients wait for a server to start, and on macOS the
.NET CLI prints "Skipping NuGet package signature verification." to stdout during that download (most clients
skip the non-JSON line). Later launches are fast and clean. The installed tool has neither issue.

The server runs in the client's working directory, so coding agents can pass repo-relative paths to
`load_project`; desktop apps usually start servers elsewhere, so use absolute paths there.

## Limits

- The session runs on the newest .NET runtime installed (pre-releases excluded), so it loads projects
  targeting that version or older. FSharp.Core in the session is 10.1; a project built against a newer one
  is reported as a conflict.
- Frameworks: `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` (not Windows Desktop).
- One session per server; calls run one at a time.

## Development

```sh
dotnet test -c Release                           # drives the real server over stdio (a few minutes)
dotnet pack src/FsiMcp -c Release -o artifacts
```

- `src/FsiMcp/`: `ProjectResolver.fs` (MSBuild → references), `Worker.fs` (the FSI host),
  `Server.fs` (MCP), `Text.fs` (capture, JSON helpers).
- `tests/FsiMcp.Tests/`: end-to-end tests over stdio; `tests/fixtures/` holds projects they build.
- `scripts/repl.py`: keeps one session alive behind a Unix socket, for driving it from a shell.
- Releases: [RELEASING.md](../RELEASING.md).
