module FsiMcp.Tests.Tests

open System
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
    Assert.Equal<string list>([ "eval"; "typecheck"; "reset"; "bindings"; "profile"; "load_project" ], names)

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

// ---------------------------------------------------------------- 0.2: prelude, cost line, options, bindings, profile

let preludePath = Path.Combine(fixturesDir, "prelude", "helpers.fsx")

[<Fact>]
let ``a prelude is loaded after every load, reset and restart`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.Call("load_project", {| path = dir; prelude = preludePath |}) |> ok |> has "prelude helpers.fsx: loaded; opened Helpers, System.Text"
    s.Eval "greet \"a\", StringBuilder(\"sb\").ToString()" |> ok |> has "(\"hi a\", \"sb\")"
    s.Reset() |> ok |> has "prelude helpers.fsx: loaded"
    s.Eval "greet \"b\"" |> ok |> has "\"hi b\""
    s.Eval("while true do ()", 2) |> failed |> has "prelude helpers.fsx: loaded"
    s.Eval "greet \"c\"" |> ok |> has "\"hi c\""
    s.Call("reset", {| prelude = "" |}) |> ok |> lacks "prelude"
    s.Eval "greet \"d\"" |> failed |> has "FS0039"

[<Fact>]
let ``slow or allocating evals report their cost`` () =
    use s = start ()
    let r = s.Eval "Array.init 3_000_000 (fun i -> string i) |> Array.length" |> ok
    has "allocated" r
    has "GCs gen0" r
    s.Eval "1 + 1" |> ok |> lacks "allocated"

[<Fact>]
let ``a failed eval says nothing from it was kept`` () =
    use s = start ()
    s.Eval "let a = 1\nlet b: int = \"x\"" |> failed |> has "Nothing from this eval was kept"
    s.Eval "a" |> failed |> has "FS0039"

[<Fact>]
let ``gc_server and env reach the session process`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.Call("load_project", {| path = dir; gc_server = true; env = {| FSIMCP_TEST = "yes" |} |}) |> ok |> has "server GC"
    s.Eval "System.Runtime.GCSettings.IsServerGC, System.Environment.GetEnvironmentVariable \"FSIMCP_TEST\"" |> ok |> has "(true, \"yes\")"

[<Fact>]
let ``bindings lists names and types`` () =
    use s = start ()
    s.Eval "let count = 3\nlet label = \"x\"" |> ok |> ignore
    let r = s.Call("bindings", (obj ())) |> ok
    has "count: int" r
    has "label: string" r

[<Fact>]
let ``profile cpu names the hot function`` () =
    use s = start ()
    s.Eval "let rec fib n = if n < 2 then n else fib (n - 1) + fib (n - 2)" |> ok |> ignore
    let r = s.Call("profile", {| code = "fib 34" |}) |> ok
    has "val it: int = 5702887" r
    has "--- profile (cpu) ---" r
    has "fib" r

[<Fact>]
let ``profile alloc reports what was allocated`` () =
    use s = start ()
    let r = s.Call("profile", {| code = "[ for i in 1 .. 2_000_000 -> string i ] |> List.length"; mode = "alloc" |}) |> ok
    has "--- profile (alloc) ---" r
    has "System.String" r

[<Fact>]
let ``the module list repeats only when it changes`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.LoadProject dir |> ok |> has "SampleLib.Lib"
    s.LoadProject dir |> ok |> has "unchanged (1)"

[<Fact>]
let ``artifacts_path keeps build output out of the project folder`` () =
    let dir = copyFixture "SampleLib"
    let artifacts = Path.Combine(Path.GetTempPath(), "fsi-mcp-tests", Guid.NewGuid().ToString("N"), "artifacts")
    use s = start ()
    s.Call("load_project", {| path = dir; artifacts_path = artifacts |}) |> ok |> ignore
    Assert.False(Directory.Exists(Path.Combine(dir, "bin")), "bin/ under the project")
    Assert.False(Directory.Exists(Path.Combine(dir, "obj")), "obj/ under the project")
    Assert.True(Directory.Exists(Path.Combine(artifacts, "bin")))
    s.Eval "SampleLib.Lib.version" |> ok |> has "val it: int = 1"

[<Fact>]
let ``load_project returns its references as structured content`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = s.LoadProject dir |> ok
    let refs = [ for e in r.Structured.Value.GetProperty("references").EnumerateArray() -> e.GetString() ]
    Assert.Contains(refs, fun p -> p.EndsWith "Newtonsoft.Json.dll")
    Assert.Contains(refs, fun p -> p.EndsWith "SampleLib.dll")
    Assert.All(refs, fun p -> Assert.True(File.Exists p, p))
    Assert.DoesNotContain(refs, fun p -> p.Contains(Path.Combine(Path.GetTempPath(), "fsi-mcp") + string Path.DirectorySeparatorChar))
