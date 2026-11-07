/// Output capture and small JSON helpers shared by the server and the worker.
module FsiMcp.Text

open System.IO
open System.Text
open System.Text.Json

/// Accumulates everything written to it until drained. Installed as Console.Out/Error so user code
/// (printfn, background threads) can never write into a protocol stream.
type Capture() =
    inherit TextWriter()
    let sb = StringBuilder()
    override _.Encoding = Encoding.UTF8
    override _.Write(c: char) = lock sb (fun () -> sb.Append(c) |> ignore)
    override _.Write(s: string) = if not (isNull s) then lock sb (fun () -> sb.Append(s) |> ignore)
    override _.Write(buffer: char[], index: int, count: int) = lock sb (fun () -> sb.Append(buffer, index, count) |> ignore)

    /// Everything written since the last drain, with Windows line endings normalised to \n.
    member _.Drain() =
        lock sb (fun () ->
            let s = sb.ToString()
            sb.Clear() |> ignore
            s.Replace("\r\n", "\n"))

/// Keep the head and the tail of long text (errors tend to be at the end).
let clip (max: int) (s: string) =
    if s.Length <= max then s
    else
        let head = max * 2 / 3
        let tail = max - head
        s.Substring(0, head) + $"\n... [{s.Length - max} chars clipped] ...\n" + s.Substring(s.Length - tail)

let str (e: JsonElement) (name: string) =
    if e.ValueKind <> JsonValueKind.Object then ""
    else
        match e.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString()
        | _ -> ""

let argInt (a: JsonElement) (name: string) (dflt: int) =
    if a.ValueKind <> JsonValueKind.Object then dflt
    else
        match a.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.Number -> v.GetInt32()
        | _ -> dflt

let argBool (a: JsonElement) (name: string) (dflt: bool) =
    if a.ValueKind <> JsonValueKind.Object then dflt
    else
        match a.TryGetProperty name with
        | true, v when v.ValueKind = JsonValueKind.True -> true
        | true, v when v.ValueKind = JsonValueKind.False -> false
        | _ -> dflt

let emptyObject = JsonDocument.Parse("{}").RootElement.Clone()

/// One JSON message per line (MCP stdio framing; also the worker protocol).
let writeLine (w: TextWriter) (msg: obj) =
    lock w (fun () ->
        w.Write(JsonSerializer.Serialize<obj>(msg))
        w.Write('\n')
        w.Flush())
