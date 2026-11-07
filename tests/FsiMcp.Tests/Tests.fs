module FsiMcp.Tests.Tests

open System.IO
open Xunit
open FsiMcp.Tests.McpClient

// Every test runs its own server + worker processes; running them in parallel only adds contention.
[<assembly: CollectionBehavior(DisableTestParallelization = true)>]
do ()

let has (expected: string) (r: ToolResult) =
    Assert.True(r.Text.Contains expected, $"expected to find\n  {expected}\nin\n{r.Text}")

let lacks (unexpected: string) (r: ToolResult) =
    Assert.False(r.Text.Contains unexpected, $"did not expect\n  {unexpected}\nin\n{r.Text}")

let ok (r: ToolResult) =
    Assert.False(r.IsError, $"expected success, got\n{r.Text}")
    r

let failed (r: ToolResult) =
    Assert.True(r.IsError, $"expected an error, got\n{r.Text}")
    r

// ---------------------------------------------------------------- protocol

[<Fact>]
let ``initialize and tools/list describe the server`` () =
    use s = new Server()
    let init = s.Request("initialize", {| protocolVersion = "2025-06-18"; capabilities = (obj ()); clientInfo = {| name = "t"; version = "0" |} |})
    Assert.Equal("fsi-mcp", init.GetProperty("serverInfo").GetProperty("name").GetString())
    Assert.Equal("2025-06-18", init.GetProperty("protocolVersion").GetString())
    let tools = s.Request("tools/list", (obj ()))
    let names = [ for t in tools.GetProperty("tools").EnumerateArray() -> t.GetProperty("name").GetString() ]
    Assert.Equal<string list>([ "eval"; "typecheck"; "reset"; "load_project" ], names)

// ---------------------------------------------------------------- eval

[<Fact>]
let ``bindings echo their type, expressions echo their value`` () =
    use s = start ()
    s.Eval "let x = 21" |> ok |> has "val x: int"
    s.Eval "let x = 21" |> lacks "= 21"
    s.Eval "x * 2" |> ok |> has "val it: int = 42"

[<Fact>]
let ``a multi-statement interaction still shows the trailing value`` () =
    use s = start ()
    let r = s.Eval "printfn \"hello\"\n[1; 2] |> List.map ((*) 2)" |> ok
    has "hello" r
    has "val it: int list = [2; 4]" r

[<Fact>]
let ``printed output is captured and never corrupts the protocol`` () =
    use s = start ()
    let r = s.Eval "printfn \"{\\\"jsonrpc\\\": \\\"fake\\\"}\"; eprintfn \"to stderr\"; System.Console.Out.Flush()" |> ok
    has "fake" r
    has "stderr:\nto stderr" r
    s.Eval "1 + 1" |> ok |> has "val it: int = 2"

[<Fact>]
let ``compile errors are errors with the compiler's message`` () =
    use s = start ()
    s.Eval "let y: int = \"text\"" |> failed |> has "FS0001"

[<Fact>]
let ``runtime exceptions are errors with the exception`` () =
    use s = start ()
    let r = s.Eval "List.head ([]: int list)" |> failed
    has "System.ArgumentException" r
    lacks "at System.Reflection." r

[<Fact>]
let ``output written by background threads arrives with the next call`` () =
    use s = start ()
    s.Eval "System.Threading.Tasks.Task.Run(fun () -> System.Threading.Thread.Sleep 100; printfn \"late line\") |> ignore" |> ok |> ignore
    s.Eval "System.Threading.Thread.Sleep 600\n1" |> ok |> has "late line"

// ---------------------------------------------------------------- typecheck

[<Fact>]
let ``typecheck infers signatures without binding anything`` () =
    use s = start ()
    s.Eval "let offset = 10" |> ok |> ignore
    let r = s.Typecheck "let f a = a + offset" |> ok
    has "val f: int -> int" r
    lacks "offset" r // earlier bindings are not reported
    s.Eval "f 1" |> failed |> has "FS0039"

[<Fact>]
let ``typecheck reports errors`` () =
    use s = start ()
    s.Typecheck "let z = undefinedThing + 1" |> failed |> has "FS0039"

// ---------------------------------------------------------------- isolation

[<Fact>]
let ``a runaway evaluation is killed and the session restarts`` () =
    use s = start ()
    s.Eval "let kept = 1" |> ok |> ignore
    s.Eval("while true do ()", 2) |> failed |> has "timed out"
    s.Eval "kept" |> failed |> has "FS0039"
    s.Eval "1 + 1" |> ok |> has "val it: int = 2"

[<Fact>]
let ``a stack overflow kills only the worker`` () =
    use s = start ()
    s.Eval "let rec f n = 1 + f (n + 1)\nf 0" |> failed |> has "worker process died"
    s.Eval "1 + 1" |> ok |> has "val it: int = 2"

[<Fact>]
let ``reset discards bindings`` () =
    use s = start ()
    s.Eval "let kept = 1" |> ok |> ignore
    s.Reset() |> ok |> has "Fresh session"
    s.Eval "kept" |> failed |> has "FS0039"

// ---------------------------------------------------------------- load_project

[<Fact>]
let ``load_project references the project and its NuGet dependencies`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.LoadProject dir |> ok |> has "SampleLib.Lib"
    s.Eval "SampleLib.Lib.roundTrip [| 1; 2; 3 |]" |> ok |> has "[|1; 2; 3|]"
    s.Eval "typeof<Newtonsoft.Json.JsonConvert>.Assembly.GetName().Name" |> ok |> has "\"Newtonsoft.Json\""

[<Fact>]
let ``rebuilt code is live after load_project`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.LoadProject dir |> ok |> ignore
    s.Eval "SampleLib.Lib.version" |> ok |> has "= 1"
    let lib = Path.Combine(dir, "Library.fs")
    File.WriteAllText(lib, File.ReadAllText(lib).Replace("let version = 1", "let version = 2\nlet added = \"new\""))
    s.LoadProject dir |> ok |> ignore
    s.Eval "SampleLib.Lib.version, SampleLib.Lib.added" |> ok |> has "(2, \"new\")"

[<Fact>]
let ``a failed build keeps the previous session`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.LoadProject dir |> ok |> ignore
    s.Eval "let kept = 5" |> ok |> ignore
    File.AppendAllText(Path.Combine(dir, "Library.fs"), "\nlet broken: int = \"text\"\n")
    let r = s.LoadProject dir |> failed
    has "FS0001" r
    lacks "\"Items\"" r // not the MSBuild item JSON dump
    s.Eval "kept, SampleLib.Lib.version" |> ok |> has "(5, 1)"

[<Fact>]
let ``load_project reports a missing path`` () =
    use s = start ()
    s.LoadProject "does/not/exist" |> failed |> has "Not found"
