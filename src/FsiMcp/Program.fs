module FsiMcp.Program

open System

let private usage =
    $"""fsi-mcp {Server.version}
An MCP server (stdio) that gives AI agents a persistent, project-aware F# Interactive session.

It is started by an MCP client, not by hand. For example, with Claude Code:
  claude mcp add fsi -- fsi-mcp

Tools: load_project, eval, typecheck, reset.
Docs: https://github.com/OnurGumus/FsiMcp

Options:
  --version   print the version
  --help      print this text"""

[<EntryPoint>]
let main argv =
    match argv with
    | [| "--worker" |] -> Worker.run ()
    | [| "--version" |] ->
        printfn "%s" Server.version
        0
    | [| "--help" |]
    | [| "-h" |] ->
        printfn "%s" usage
        0
    | [||] ->
        if not Console.IsInputRedirected then
            eprintfn "fsi-mcp %s: MCP server waiting for JSON-RPC on stdin. Run it from an MCP client; see --help." Server.version
        Server.run ()
    | _ ->
        eprintfn "Unknown arguments: %s\n\n%s" (String.Join(" ", argv)) usage
        2
