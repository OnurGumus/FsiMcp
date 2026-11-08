/// A minimal MCP client that runs the real server process over stdio.
module FsiMcp.Tests.McpClient

open System
open System.Diagnostics
open System.IO
open System.Reflection
open System.Text
open System.Text.Json

let private metadata key =
    Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    |> Seq.find (fun a -> a.Key = key)
    |> fun a -> a.Value

let serverDll = metadata "ServerDll"
let fixturesDir = metadata "FixturesDir"

type ToolResult = { Text: string; IsError: bool; Structured: JsonElement option }

/// `env`: variables set in the server's environment.
type Server(?cwd: string, ?env: (string * string) list) =
    let stderr = StringBuilder()
    let p =
        let psi = ProcessStartInfo("dotnet")
        psi.ArgumentList.Add serverDll
        psi.UseShellExecute <- false
        psi.RedirectStandardInput <- true
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.StandardInputEncoding <- UTF8Encoding(false)
        psi.StandardOutputEncoding <- UTF8Encoding(false)
        psi.WorkingDirectory <- defaultArg cwd Environment.CurrentDirectory
        for k, v in defaultArg env [] do psi.Environment[k] <- v
        let p = Process.Start psi
        p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then lock stderr (fun () -> stderr.AppendLine e.Data |> ignore))
        p.BeginErrorReadLine()
        p
    let mutable nextId = 0

    let write (msg: obj) =
        p.StandardInput.Write(JsonSerializer.Serialize<obj>(msg))
        p.StandardInput.Write('\n')
        p.StandardInput.Flush()

    // Responses that arrived while waiting for another id.
    let early = System.Collections.Generic.Dictionary<int, string>()

    /// Send a request without waiting; returns its id (see Await).
    member _.Send(``method``: string, prms: obj) : int =
        nextId <- nextId + 1
        write {| jsonrpc = "2.0"; id = nextId; ``method`` = ``method``; ``params`` = prms |}
        nextId

    /// Wait for the response to request `id` and return its `result`. Every line the server writes must be
    /// JSON: anything else (a stray printfn) throws here, which is how the tests check that the protocol
    /// stream stays clean.
    member _.Await(id: int, ``method``: string, ?timeout: TimeSpan) : JsonElement =
        let deadline = DateTime.UtcNow + defaultArg timeout (TimeSpan.FromSeconds 180.)
        let resultOf (line: string) =
            use doc = JsonDocument.Parse line
            match doc.RootElement.TryGetProperty "result" with
            | true, result -> result.Clone()
            | _ -> failwith $"error response: {line}"
        let rec loop () =
            let read = p.StandardOutput.ReadLineAsync()
            let remaining = deadline - DateTime.UtcNow
            if remaining <= TimeSpan.Zero || not (read.Wait remaining) then
                failwith $"no response to {``method``} in time; server stderr:\n{stderr}"
            match read.Result with
            | null -> failwith $"server exited; stderr:\n{stderr}"
            | line ->
                use doc = JsonDocument.Parse line
                match doc.RootElement.TryGetProperty "id" with
                | true, v when v.ValueKind = JsonValueKind.Number ->
                    if v.GetInt32() = id then resultOf line
                    else
                        early[v.GetInt32()] <- line
                        loop ()
                | _ -> loop ()
        match early.TryGetValue id with
        | true, line ->
            early.Remove id |> ignore
            resultOf line
        | _ -> loop ()

    /// Send a request and return its `result`.
    member this.Request(``method``: string, prms: obj, ?timeout: TimeSpan) : JsonElement =
        let id = this.Send(``method``, prms)
        match timeout with
        | Some t -> this.Await(id, ``method``, t)
        | None -> this.Await(id, ``method``)

    member _.Notify(``method``: string, prms: obj) = write {| jsonrpc = "2.0"; ``method`` = ``method``; ``params`` = prms |}

    static member ToToolResult(r: JsonElement) : ToolResult =
        { Text = r.GetProperty("content").Item(0).GetProperty("text").GetString()
          IsError = r.GetProperty("isError").GetBoolean()
          Structured =
            match r.TryGetProperty "structuredContent" with
            | true, v -> Some v
            | _ -> None }

    member this.Call(tool: string, args: obj) : ToolResult =
        Server.ToToolResult(this.Request("tools/call", {| name = tool; arguments = args |}))

    member this.Eval(code: string) = this.Call("eval", {| code = code |})
    member this.Eval(code: string, timeoutSeconds: int) = this.Call("eval", {| code = code; timeout_seconds = timeoutSeconds |})
    member this.Typecheck(code: string) = this.Call("typecheck", {| code = code |})
    member this.Reset() = this.Call("reset", (obj ()))
    member this.LoadProject(path: string) = this.Call("load_project", {| path = path |})

    interface IDisposable with
        member _.Dispose() =
            try
                p.StandardInput.Close()
                if not (p.WaitForExit 15000) then p.Kill true
            with _ -> ()

    member this.Start() =
        this.Request("initialize", {| protocolVersion = "2025-06-18"; capabilities = (obj ()); clientInfo = {| name = "tests"; version = "0" |} |})
        |> ignore
        this.Notify("notifications/initialized", (obj ()))
        this

let start () = (new Server()).Start()

/// Copy a fixture project to a fresh temp directory (tests build and edit it there).
let copyFixture (name: string) =
    let src = Path.Combine(fixturesDir, name)
    let dest = Path.Combine(Path.GetTempPath(), "fsi-mcp-tests", Guid.NewGuid().ToString("N"), name)
    Directory.CreateDirectory dest |> ignore
    for f in Directory.GetFiles src do
        File.Copy(f, Path.Combine(dest, Path.GetFileName f))
    dest

/// Copy several fixture projects side by side into one fresh temp root (for project references); returns the root.
let copyFixtures (names: string list) =
    let root = Path.Combine(Path.GetTempPath(), "fsi-mcp-tests", Guid.NewGuid().ToString("N"))
    for name in names do
        let src = Path.Combine(fixturesDir, name)
        let dest = Path.Combine(root, name)
        Directory.CreateDirectory dest |> ignore
        for f in Directory.GetFiles src do
            File.Copy(f, Path.Combine(dest, Path.GetFileName f))
    root
