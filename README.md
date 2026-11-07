# FsiMcp

**Give your AI agent an F# REPL that knows your project.**

FsiMcp is an [MCP](https://modelcontextprotocol.io) server that lets Claude, Copilot, Cursor and friends work
in F# the way you do: load the project, run a function, look at the result, build on it. No throwaway
scripts, no rerunning everything for each new question.

```
load_project src/Engine              # your code + every dependency, ready in seconds
eval  let r = Engine.solve input     # run once, keep it in memory
eval  r.Items |> List.countBy kind   # follow-up questions in milliseconds
typecheck  let f x = ...             # check against your types without running
```

**3× faster investigations.** In a real 9-step debugging session on a production F# library, FsiMcp took
8.8 s where rerunning scripts took 27.2 s. Follow-up questions came back in 8–40 ms instead of 2–5 s.

## Quick start

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
claude mcp add --scope user fsi -- dnx FsiMcp --yes
```

Any other MCP client: run `dnx FsiMcp --yes` as a stdio server
([config snippets](https://github.com/OnurGumus/FsiMcp/blob/main/docs/how-it-works.md#client-configuration)),
or `dotnet tool install -g FsiMcp` and run `fsi-mcp`.

## Built for real projects

- **Loads anything.** NuGet packages, project references, native libraries, ASP.NET Core, resolved straight
  from MSBuild. No hand-maintained `references.fsx`.
- **Rebuild and keep going.** Edit your code, call `load_project` again, and the new code is live.
- **Can't hang.** Infinite loops time out and stack overflows are survived: the session restarts, the server
  doesn't.
- **Light on context.** Types for bindings, values on request, long output clipped, and `printfn` never
  breaks the protocol.
- **All of FSI.** `#r "nuget: ..."`, `#load`, `open`, everything `dotnet fsi` does.

## Tools

| | |
| --- | --- |
| `load_project` | build a project and start a session with it loaded |
| `eval` | run F#; state persists between calls |
| `typecheck` | check code against the session without running it |
| `reset` | fresh session, same project |

[How it works](https://github.com/OnurGumus/FsiMcp/blob/main/docs/how-it-works.md) ·
[Releasing](https://github.com/OnurGumus/FsiMcp/blob/main/RELEASING.md) · MIT licensed
