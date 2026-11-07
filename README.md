# FsiMcp

A persistent, project-aware **F# Interactive** session for AI agents, served over the
[Model Context Protocol](https://modelcontextprotocol.io).

Agents usually probe code the slow way: write a script or a throwaway console project, run it, read the
output, edit, and run *everything* again. FsiMcp lets an agent work the way a person works at `dotnet fsi`:
load the project, define a function, call it, look at the intermediate result, then build the next step on
top of what is already in memory.

```
load_project src/Engine/Engine.fsproj     → references the build output + every dependency
eval  let result = Engine.solve input     → computed once (seconds), kept in memory
eval  result.Items |> List.countBy shape  → 20 ms
eval  let leftover = ...                  → builds on earlier bindings
typecheck  let f x = ...                  → "does this compile against the project?" without running it
```

Measured on a real 9-step investigation of a production F# library, replayed as scripts that rerun all
earlier steps:

| | whole investigation | a follow-up question about an earlier result |
| --- | --- | --- |
| FsiMcp session | 8.8 s | 8–40 ms |
| re-running scripts (`dotnet fsi`) | 27.2 s (3.1×) | 2.2–5.1 s |

The gap grows with the cost of the computation being inspected and with the number of questions asked about it.

## Install

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or newer).

Run it without installing (`dnx` ships with the .NET 10 SDK):

```sh
dnx FsiMcp --yes
```

Or install it as a global tool, which puts `fsi-mcp` on your `PATH`:

```sh
dotnet tool install -g FsiMcp
```

## Configure your MCP client

**Claude Code**

```sh
claude mcp add --scope user fsi -- dnx FsiMcp --yes
# or, with the global tool installed:
claude mcp add --scope user fsi -- fsi-mcp
```

**VS Code** (`.vscode/mcp.json`)

```json
{
  "servers": {
    "fsi": { "type": "stdio", "command": "dnx", "args": ["FsiMcp", "--yes"] }
  }
}
```

**Claude Desktop, Cursor and other `mcpServers`-style clients**

```json
{
  "mcpServers": {
    "fsi": { "command": "dnx", "args": ["FsiMcp", "--yes"] }
  }
}
```

The server runs in the client's working directory, so `load_project` accepts repo-relative paths in coding
agents; desktop apps usually start servers elsewhere, so use absolute paths there.

## Tools

| tool | what it does |
| --- | --- |
| `load_project` | Builds a `.fsproj`/`.csproj` and starts a fresh session referencing its output and full runtime closure. Lists the assembly's public modules. Call it again after editing the project: the new session sees the rebuilt code. A failed build returns the compiler errors and keeps the current session. |
| `eval` | Runs F# in the session. Bindings, types, `open`s and `#r` persist. Returns printed output, `val x: T` per binding, `val it: T = value` for a trailing expression, and diagnostics. `#r "nuget: ..."` and `#load` work. |
| `typecheck` | Checks code against the session (project + earlier bindings) without running it. Returns inferred signatures or compiler errors. |
| `reset` | Fresh session with the same references, no rebuild. |

`eval` takes an optional `timeout_seconds` (default 120). When it runs out, or the code overflows the stack,
the session is killed and replaced; the server keeps running and says what was lost.

## How it works

**Two processes.** The MCP server owns a worker process (`fsi-mcp --worker`) that hosts
`FsiEvaluationSession` from FSharp.Compiler.Service. .NET cannot unload assemblies from the default load
context or stop a runaway thread, so an in-process host would keep running the *old* dll after a rebuild and
could hang forever on `while true do ()`. Replacing the worker solves both, in about a second.

**References come from MSBuild, not path rewriting.** `load_project` runs

```
dotnet msbuild <project> -restore -t:Build -getProperty:TargetPath -getItem:ReferencePath
    -getItem:ReferenceCopyLocalPaths -getItem:RuntimeCopyLocalItems
    -getItem:RuntimeTargetsCopyLocalItems -getItem:NativeCopyLocalItems
```

and turns the items into references, which is where hand-written `references.fsx` files (pointing at
metadata-only reference assemblies, hard-coded TFMs and machine paths) tend to break:

- `-getItem` without a target only *evaluates* the project; these items are filled by targets.
- A library's `ReferenceCopyLocalPaths` has no NuGet packages (libraries don't copy them to `bin`);
  `RuntimeCopyLocalItems` does.
- A portable app's closure lists `lib/` *and* every `runtimes/<rid>/lib/` variant of an assembly; the one for
  this machine's RID is chosen (normally `deps.json` does that at startup).
- Native libraries (e.g. `lmdb.dylib`, `e_sqlite3`) are served through `AssemblyLoadContext.ResolvingUnmanagedDll`.
- `FrameworkReference Microsoft.AspNetCore.App` maps to the installed ASP.NET Core shared framework.
- The project's own build outputs are copied to a temp folder before loading, so a session never holds files
  a rebuild writes to (Windows locks loaded dlls; on Unix a rebuild truncates mapped files).

**Output is sized for an LLM.** `let` bindings echo only their type, values print with limits (20 items,
depth 6), long output keeps its head and tail, and `printfn` from user code or background threads can never
reach the JSON-RPC stream: it is captured per call, and late output arrives with the next call.

## Limits

- The session runs on the newest .NET runtime installed (pre-releases excluded), so it can load projects
  targeting that version or older. FSharp.Core in the session is 10.1; a project built against a newer one
  is reported as a conflict.
- Frameworks: `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` (not Windows Desktop).
- One session per server; calls run one at a time.

## Development

```sh
dotnet test -c Release        # builds, then drives the real server over stdio (~40 s)
dotnet pack src/FsiMcp -c Release -o artifacts
```

- `src/FsiMcp/`: `ProjectResolver.fs` (MSBuild → references), `Worker.fs` (the FSI host),
  `Server.fs` (MCP), `Text.fs` (capture, JSON helpers).
- `tests/FsiMcp.Tests/`: end-to-end tests over stdio; `tests/fixtures/` holds projects they build.
- `scripts/repl.py`: keeps one session alive behind a Unix socket, for driving it from a shell (useful for
  agents without MCP support, and for measuring).

Releases: see [RELEASING.md](RELEASING.md).

## License

[MIT](LICENSE)
