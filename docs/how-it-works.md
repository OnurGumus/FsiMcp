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

## Client configuration

**Claude Code**

```sh
claude mcp add --scope user fsi -- dnx FsiMcp --yes
```

**VS Code** (`.vscode/mcp.json`)

```json
{ "servers": { "fsi": { "type": "stdio", "command": "dnx", "args": ["FsiMcp", "--yes"] } } }
```

**Claude Desktop, Cursor and other `mcpServers` clients**

```json
{ "mcpServers": { "fsi": { "command": "dnx", "args": ["FsiMcp", "--yes"] } } }
```

With `dotnet tool install -g FsiMcp`, use the command `fsi-mcp` instead of `dnx FsiMcp --yes`.

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
dotnet test -c Release                           # drives the real server over stdio (~40 s)
dotnet pack src/FsiMcp -c Release -o artifacts
```

- `src/FsiMcp/`: `ProjectResolver.fs` (MSBuild → references), `Worker.fs` (the FSI host),
  `Server.fs` (MCP), `Text.fs` (capture, JSON helpers).
- `tests/FsiMcp.Tests/`: end-to-end tests over stdio; `tests/fixtures/` holds projects they build.
- `scripts/repl.py`: keeps one session alive behind a Unix socket, for driving it from a shell.
- Releases: [RELEASING.md](../RELEASING.md).
