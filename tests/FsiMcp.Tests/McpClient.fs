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

type Server(?cwd: string) =
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
        let p = Process.Start psi
        p.ErrorDataReceived.Add(fun e -> if not (isNull e.Data) then lock stderr (fun () -> stderr.AppendLine e.Data |> ignore))
        p.BeginErrorReadLine()
        p
    let mutable nextId = 0

    let write (msg: obj) =
        p.StandardInput.Write(JsonSerializer.Serialize<obj>(msg))
        p.StandardInput.Write('\n')
        p.StandardInput.Flush()

    /// Send a request and return its `result`. Every line the server writes must be JSON: anything else
    /// (a stray printfn) throws here, which is how the tests check that the protocol stream stays clean.
    member _.Request(``method``: string, prms: obj, ?timeout: TimeSpan) : JsonElement =
        nextId <- nextId + 1
        let id = nextId
        write {| jsonrpc = "2.0"; id = id; ``method`` = ``method``; ``params`` = prms |}
        let deadline = DateTime.UtcNow + defaultArg timeout (TimeSpan.FromSeconds 180.)
        let rec loop () =
            let read = p.StandardOutput.ReadLineAsync()
            let remaining = deadline - DateTime.UtcNow
            if remaining <= TimeSpan.Zero || not (read.Wait remaining) then
                failwith $"no response to {``method``} in time; server stderr:\n{stderr}"
            match read.Result with
            | null -> failwith $"server exited; stderr:\n{stderr}"
            | line ->
                use doc = JsonDocument.Parse line
                let r = doc.RootElement
                match r.TryGetProperty "id" with
                | true, v when v.ValueKind = JsonValueKind.Number && v.GetInt32() = id ->
                    match r.TryGetProperty "result" with
                    | true, result -> result.Clone()
                    | _ -> failwith $"error response: {line}"
                | _ -> loop ()
        loop ()

    member _.Notify(``method``: string, prms: obj) = write {| jsonrpc = "2.0"; ``method`` = ``method``; ``params`` = prms |}

    member this.Call(tool: string, args: obj) : ToolResult =
        let r = this.Request("tools/call", {| name = tool; arguments = args |})
        { Text = r.GetProperty("content").Item(0).GetProperty("text").GetString()
          IsError = r.GetProperty("isError").GetBoolean()
          Structured =
            match r.TryGetProperty "structuredContent" with
            | true, v -> Some v
            | _ -> None }

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
