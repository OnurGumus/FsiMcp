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
    Assert.Equal<string list>([ "eval"; "typecheck"; "reset"; "bindings"; "profile"; "load_project"; "check_project"; "measure" ], names)

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
let ``load_project returns its references as structured content when asked`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = s.Call("load_project", {| path = dir; return_references = true |}) |> ok
    let refs = [ for e in r.Structured.Value.GetProperty("references").EnumerateArray() -> e.GetString() ]
    Assert.Contains(refs, fun p -> p.EndsWith "Newtonsoft.Json.dll")
    Assert.Contains(refs, fun p -> p.EndsWith "SampleLib.dll")
    Assert.All(refs, fun p -> Assert.True(File.Exists p, p))
    Assert.DoesNotContain(refs, fun p -> p.Contains(Path.Combine(Path.GetTempPath(), "fsi-mcp") + string Path.DirectorySeparatorChar))
    Assert.True(File.Exists(r.Structured.Value.GetProperty("targetPath").GetString()))

[<Fact>]
let ``load_project without return_references and eval carry no structured content`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = s.LoadProject dir |> ok
    Assert.True(r.Structured.IsNone, "load_project returned structuredContent")
    Assert.True((s.Eval "1 + 1" |> ok).Structured.IsNone, "eval returned structuredContent")
    Assert.True((s.Eval "let broken: int = \"x\"" |> failed).Structured.IsNone, "a failed eval returned structuredContent")

// ---------------------------------------------------------------- 0.4: structured eval and prelude outcome

let structuredEval (s: Server) (code: string) =
    s.Call("eval", {| code = code; return_structured = true |})

[<Fact>]
let ``eval structured: ok and stats`` () =
    use s = start ()
    let plainText = (s.Eval "[1..1000] |> List.sum" |> ok).Text
    let r = structuredEval s "[1..1000] |> List.sum" |> ok
    // The text may end with the cost line (shown only for slow evals), whose numbers differ per call.
    let body (t: string) = t.Split('\n') |> Array.filter (fun l -> not (l.StartsWith "[" && l.Contains "allocated")) |> String.concat "\n"
    Assert.Equal(body plainText, body r.Text)
    let sc = r.Structured.Value
    Assert.True(sc.GetProperty("ok").GetBoolean())
    Assert.False(sc.GetProperty("restarted").GetBoolean())
    let st = sc.GetProperty("stats")
    Assert.True(st.GetProperty("elapsedMs").GetDouble() >= 0.0)
    Assert.True(st.GetProperty("allocatedBytes").GetDouble() >= 0.0)
    for g in [ "gen0"; "gen1"; "gen2" ] do
        Assert.True(st.GetProperty(g).GetInt32() >= 0)

[<Fact>]
let ``eval structured: a failed eval is not ok`` () =
    use s = start ()
    let r = structuredEval s "let x : int = \"s\"" |> failed
    let sc = r.Structured.Value
    Assert.False(sc.GetProperty("ok").GetBoolean())
    Assert.False(sc.GetProperty("restarted").GetBoolean())
    Assert.True(sc.GetProperty("stats").GetProperty("allocatedBytes").GetDouble() >= 0.0)

[<Fact>]
let ``eval structured: a timeout reports restarted`` () =
    use s = start ()
    let r = s.Call("eval", {| code = "while true do ()"; timeout_seconds = 2; return_structured = true |}) |> failed
    let sc = r.Structured.Value
    Assert.False(sc.GetProperty("ok").GetBoolean())
    Assert.True(sc.GetProperty("restarted").GetBoolean())
    // The time until the session was lost, not the restart after it.
    let elapsed = sc.GetProperty("stats").GetProperty("elapsedMs").GetDouble()
    Assert.True(elapsed >= 1900.0 && elapsed < 2900.0, $"elapsedMs {elapsed}\n{r.Text}")

[<Fact>]
let ``eval without the flag has no structured content`` () =
    use s = start ()
    Assert.True((s.Eval "1 + 1" |> ok).Structured.IsNone)
    Assert.True((s.Call("eval", {| code = "1 + 1"; return_structured = false |}) |> ok).Structured.IsNone)

[<Fact>]
let ``load_project structured: a broken prelude is reported`` () =
    let dir = copyFixture "SampleLib"
    let bad = Path.Combine(dir, "broken.fsx")
    File.WriteAllText(bad, "let x : int = \"s\"\n")
    use s = start ()
    let r = s.Call("load_project", {| path = dir; prelude = bad; return_references = true |}) |> ok
    let p = r.Structured.Value.GetProperty("prelude")
    Assert.False(p.GetProperty("loaded").GetBoolean())
    Assert.Contains("FAILED", p.GetProperty("message").GetString())

[<Fact>]
let ``load_project structured: a good prelude is loaded`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = s.Call("load_project", {| path = dir; prelude = preludePath; return_references = true |}) |> ok
    let p = r.Structured.Value.GetProperty("prelude")
    Assert.True(p.GetProperty("loaded").GetBoolean())
    Assert.Contains("loaded", p.GetProperty("message").GetString())
    Assert.True(p.GetProperty("seconds").GetDouble() >= 0.0)

[<Fact>]
let ``a prelude with a dotted file name opens as a module path`` () =
    let dir = copyFixture "SampleLib"
    let f = Path.Combine(dir, "lab.setup.fsx")
    File.WriteAllText(f, "let sample = 41\n")
    use s = start ()
    s.Call("load_project", {| path = dir; prelude = f |}) |> ok |> lacks "could not open" |> ignore
    s.Eval "sample" |> ok |> has "val it: int = 41"

[<Fact>]
let ``a prelude whose name needs quoting still opens`` () =
    let dir = copyFixture "SampleLib"
    let f = Path.Combine(dir, "my-helpers.fsx")
    File.WriteAllText(f, "let sample = 42\n")
    use s = start ()
    s.Call("load_project", {| path = dir; prelude = f |}) |> ok |> lacks "could not open" |> ignore
    s.Eval "sample" |> ok |> has "val it: int = 42"

[<Fact>]
let ``load_project structured: a slow prelude reports its own load time`` () =
    let dir = copyFixture "SampleLib"
    let slow = Path.Combine(dir, "slow.fsx")
    File.WriteAllText(slow, "System.Threading.Thread.Sleep 1200\nlet slowValue = 1\n")
    use s = start ()
    let r = s.Call("load_project", {| path = dir; prelude = slow; return_references = true |}) |> ok
    let p = r.Structured.Value.GetProperty("prelude")
    Assert.True(p.GetProperty("loaded").GetBoolean())
    Assert.True(p.GetProperty("seconds").GetDouble() >= 1.0)

[<Fact>]
let ``load_project structured: no prelude is null`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = s.Call("load_project", {| path = dir; return_references = true |}) |> ok
    Assert.Equal(System.Text.Json.JsonValueKind.Null, r.Structured.Value.GetProperty("prelude").ValueKind)

// ---------------------------------------------------------------- measure

let measure (s: Server) (expr: string) = s.Call("measure", {| expr = expr |})

let bytesPerCall (r: ToolResult) = r.Structured.Value.GetProperty("bytesPerCall").GetDouble()

[<Fact>]
let ``measure: a list costs more than an array`` () =
    use s = start ()
    let list = measure s "List.init 100 id |> List.sum" |> ok
    let array = measure s "Array.init 100 id |> Array.sum" |> ok
    has " per call · " list
    has " B per call · GCs gen0 " list
    Assert.True(bytesPerCall list > bytesPerCall array, $"list {bytesPerCall list} B, array {bytesPerCall array} B")
    let sc = list.Structured.Value
    Assert.True(sc.GetProperty("medianNs").GetDouble() > 0.0)
    Assert.True(sc.GetProperty("p90Ns").GetDouble() >= sc.GetProperty("medianNs").GetDouble())
    Assert.True(sc.GetProperty("iterations").GetInt64() >= 10L)
    Assert.False(sc.GetProperty("gcServer").GetBoolean())

[<Fact>]
let ``measure: no allocation reads 0 B`` () =
    use s = start ()
    let r = measure s "1 + 1" |> ok
    Assert.True(bytesPerCall r < 1.0, $"1 + 1 allocated {bytesPerCall r} B per call\n{r.Text}")

[<Fact>]
let ``measure: iterations sets the number of calls`` () =
    use s = start ()
    let r = s.Call("measure", {| expr = "Array.zeroCreate<int> 10"; iterations = 500 |}) |> ok
    Assert.Equal(500, r.Structured.Value.GetProperty("iterations").GetInt32())
    has "over 500 calls" r

[<Fact>]
let ``measure: fewer than 10 iterations run 10`` () =
    use s = start ()
    let r = s.Call("measure", {| expr = "1 + 1"; iterations = 3 |}) |> ok
    Assert.Equal(10, r.Structured.Value.GetProperty("iterations").GetInt32())

[<Fact>]
let ``measure: negative iterations run 10`` () =
    use s = start ()
    let r = s.Call("measure", {| expr = "1 + 1"; iterations = -5 |}) |> ok
    Assert.Equal(10L, r.Structured.Value.GetProperty("iterations").GetInt64())

let median (r: ToolResult) = r.Structured.Value.GetProperty("medianNs").GetDouble()

[<Fact>]
let ``measure: the same expression twice reads about the same`` () =
    use s = start ()
    // Whether .NET puts this array on the stack depends on when the JIT gets to it: compare times only.
    let expr = "Array.init 100 id |> Array.sum"
    let a = measure s expr |> ok
    let b = measure s expr |> ok
    let ratio = max (median a) (median b) / min (median a) (median b)
    Assert.True(ratio <= 3.0, $"medians {median a} ns and {median b} ns\n{a.Text}\n{b.Text}")

[<Fact>]
let ``measure: the same allocating expression twice allocates the same`` () =
    use s = start ()
    let expr = "List.init 100 id |> List.sum" // allocates its 100 cells whatever the JIT does
    let a = measure s expr |> ok
    let b = measure s expr |> ok
    Assert.True(abs (bytesPerCall a - bytesPerCall b) < 64.0, $"bytes {bytesPerCall a} and {bytesPerCall b}\n{a.Text}\n{b.Text}")

[<Fact>]
let ``measure: a slow expression fits the default timeout and keeps the session`` () =
    use s = start ()
    s.Eval "let before = 42" |> ok |> ignore
    let r = s.Call("measure", {| expr = "System.Threading.Thread.Sleep 300"; iterations = 10 |}) |> ok
    Assert.Equal(10L, r.Structured.Value.GetProperty("iterations").GetInt64())
    Assert.True(median r >= 250e6, $"median {median r} ns\n{r.Text}")
    s.Eval "before" |> ok |> has "val it: int = 42"

[<Fact>]
let ``measure: a generic struct result needs a type annotation`` () =
    use s = start ()
    measure s "Ok 5" |> failed |> has "add a type annotation"
    let r = measure s "(Ok 5 : Result<int, string>)" |> ok
    Assert.True(bytesPerCall r < 1.0, $"{bytesPerCall r} B per call\n{r.Text}")

[<Fact>]
let ``measure: a struct anonymous record result needs a type annotation`` () =
    use s = start ()
    measure s "struct {| X = None |}" |> failed |> has "add a type annotation"

[<Fact>]
let ``measure: session bindings are usable`` () =
    use s = start ()
    s.Eval "let n = 50" |> ok |> ignore
    measure s "List.init n id" |> ok |> has "per call"

[<Fact>]
let ``measure: a throwing expression is an error and keeps the session`` () =
    use s = start ()
    s.Eval "let n = 50" |> ok |> ignore
    measure s "failwith \"boom\"" |> failed |> has "boom"
    s.Eval "n" |> ok |> has "val it: int = 50"

[<Fact>]
let ``measure: a compile error is an error with the diagnostics`` () =
    use s = start ()
    measure s "undefinedThing + 1" |> failed |> has "(1,1): error FS0039" // positions are the expression's own
    measure s "1 +\n  undefinedThing" |> failed |> has "(2,3): error FS0039"

[<Fact>]
let ``measure: a function value is refused`` () =
    use s = start ()
    measure s "fun x -> x + 1" |> failed |> has "not a function"
    measure s "id" |> failed |> has "not a function"

[<Fact>]
let ``measure: a generic result is measured`` () =
    use s = start ()
    measure s "[]" |> ok |> has "per call"

[<Fact>]
let ``measure: a timeout restarts the session`` () =
    use s = start ()
    let r = s.Call("measure", {| expr = "System.Threading.Thread.Sleep 1000"; timeout_seconds = 2 |}) |> failed
    has "timed out" r
    Assert.True(r.Structured.Value.GetProperty("restarted").GetBoolean())
    s.Eval "1 + 1" |> ok |> has "val it: int = 2"

[<Fact>]
let ``measure: an error that keeps the session has no structured content`` () =
    use s = start ()
    Assert.True((measure s "undefinedThing + 1" |> failed).Structured.IsNone)

[<Fact>]
let ``measure: the note says other threads are not counted`` () =
    use s = start ()
    let r = measure s "1 + 1" |> ok
    Assert.Equal("Allocations on other threads (async, Task, Parallel) are not counted.", r.Structured.Value.GetProperty("note").GetString())

[<Fact>]
let ``measure: nothing new is bound`` () =
    use s = start ()
    s.Eval "let kept = 7\nkept + 1" |> ok |> ignore
    measure s "printfn \"noise\"; kept" |> ok |> lacks "noise"
    s.Call("bindings", (obj ())) |> ok |> lacks "__fsimcp_measure_"
    let r = s.Eval "it" |> ok
    has "val it: int = 8" r
    lacks "noise" r
    lacks "__fsimcp_measure_" r

// ---------------------------------------------------------------- check_project

let checkOn (s: Server) (project: string) (overlays: obj[]) =
    s.Call("check_project", {| project = project; artifacts_path = Path.Combine(project, "..", "artifacts"); overlays = overlays |})

let errors (r: ToolResult) =
    [ for d in r.Structured.Value.GetProperty("diagnostics").EnumerateArray() do
        if d.GetProperty("severity").GetString() = "error" then yield d ]

let where (d: Text.Json.JsonElement) =
    d.GetProperty("path").GetString(), d.GetProperty("line").GetInt32(), d.GetProperty("code").GetString()

let badLibrary = "module SampleLib.Lib\nlet version : int = \"x\"\n"

[<Fact>]
let ``check_project: a clean project has no errors and nothing is written under it`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = checkOn s dir [||] |> ok
    Assert.Empty(errors r)
    has "0 errors" r
    Assert.False(Directory.Exists(Path.Combine(dir, "obj")))
    Assert.False(Directory.Exists(Path.Combine(dir, "bin")))

[<Fact>]
let ``check_project: an overlay replaces the file, and the session is untouched`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.Eval "let kept = 42" |> ok |> ignore
    let r = checkOn s dir [| {| path = Path.Combine(dir, "Library.fs"); content = badLibrary |} |] |> ok
    has "1 errors" r
    has "Library.fs:2:" r
    let e = (errors r).Head
    Assert.Equal(("Library.fs", 2, "FS0001"), where e)
    Assert.Equal(21, e.GetProperty("column").GetInt32())
    s.Eval "kept" |> ok |> has "val it: int = 42"

[<Fact>]
let ``check_project: overlay paths may be relative to the project folder`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = checkOn s dir [| {| path = "Library.fs"; content = badLibrary |} |] |> ok
    has "1 errors" r
    Assert.Equal<(string * int * string) list>([ "Library.fs", 2, "FS0001" ], errors r |> List.map where)

[<Fact>]
let ``check_project: an overlay outside the project is checked last`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = checkOn s dir [| {| path = "New.fs"; content = "module SampleLib.New\nlet v : int = SampleLib.Lib.version\n" |} |] |> ok
    Assert.Empty(errors r)

[<Fact>]
let ``check_project: an added overlay is checked too`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    let r = checkOn s dir [| {| path = "New.fs"; content = "module SampleLib.New\nlet v : string = SampleLib.Lib.version\n" |} |] |> ok
    Assert.Equal<(string * int * string) list>([ "New.fs", 2, "FS0001" ], errors r |> List.map where)

[<Fact>]
let ``check_project: each check sees the current overlays and the files on disk`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    checkOn s dir [| {| path = "Library.fs"; content = badLibrary |} |] |> ok |> has "1 errors" // overlaid
    Assert.Empty(errors (checkOn s dir [||] |> ok)) // overlay gone: the file on disk again
    File.WriteAllText(Path.Combine(dir, "Library.fs"), "module SampleLib.Lib\nlet version = 1\nlet bad : string = 2\n")
    let r = checkOn s dir [||] |> ok // edited on disk
    Assert.Equal<(string * int * string) list>([ "Library.fs", 3, "FS0001" ], errors r |> List.map where)

[<Fact>]
let ``check_project works on an up-to-date project`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    s.Call("load_project", {| path = dir; artifacts_path = Path.Combine(dir, "..", "artifacts") |}) |> ok |> ignore
    Assert.Empty(errors (checkOn s dir [||] |> ok))
    let r = checkOn s dir [| {| path = "Library.fs"; content = badLibrary |} |] |> ok
    Assert.Equal<(string * int * string) list>([ "Library.fs", 2, "FS0001" ], errors r |> List.map where)

[<Fact>]
let ``check_project checks a project whose reference was never built`` () =
    let root = copyFixtures [ "SampleLib"; "AppWithRef" ]
    use s = start ()
    let r = checkOn s (Path.Combine(root, "AppWithRef")) [||] |> ok
    Assert.Empty(errors r)

[<Fact>]
let ``check_project checks a broken project whose reference was never built`` () =
    let root = copyFixtures [ "SampleLib"; "AppWithRef" ]
    File.WriteAllText(Path.Combine(root, "AppWithRef", "Program.fs"), "module AppWithRef.Program\nlet doubled : string = SampleLib.Lib.version * 2\n")
    use s = start ()
    let r = checkOn s (Path.Combine(root, "AppWithRef")) [||] |> ok
    Assert.Equal<(string * int * string) list>([ "Program.fs", 2, "FS0001" ], errors r |> List.map where)

[<Fact>]
let ``check_project refreshes its arguments after the project file changes`` () =
    let dir = copyFixture "SampleLib"
    use s = start ()
    Assert.Empty(errors (checkOn s dir [||] |> ok))
    let proj = Path.Combine(dir, "SampleLib.fsproj")
    File.WriteAllText(proj, File.ReadAllText(proj).Replace("""<Compile Include="Library.fs" />""", """<Compile Include="Library.fs" /><Compile Include="Extra.fs" />"""))
    File.WriteAllText(Path.Combine(dir, "Extra.fs"), "module SampleLib.Extra\nlet bad : int = \"x\"\n")
    let r = checkOn s dir [||] |> ok
    Assert.Equal<(string * int * string) list>([ "Extra.fs", 2, "FS0001" ], errors r |> List.map where)

[<Fact>]
let ``check_project reports a failed MSBuild run as an error`` () =
    let dir = copyFixture "SampleLib"
    let proj = Path.Combine(dir, "SampleLib.fsproj")
    File.WriteAllText(proj, File.ReadAllText(proj).Replace("13.0.3", "999.0.0-nope"))
    use s = start ()
    // NU1102 when the package source answers, NU1301 offline: either way the arguments could not be read
    checkOn s dir [||] |> failed |> has "Could not get the compiler arguments"

[<Fact>]
let ``check_project uses the artifacts path remembered from load_project`` () =
    let dir = copyFixture "SampleLib"
    let artifacts = Path.Combine(dir, "..", "artifacts")
    use s = start ()
    s.Call("load_project", {| path = dir; artifacts_path = artifacts |}) |> ok |> ignore
    let r = s.Call("check_project", {| project = dir; overlays = [| {| path = "Library.fs"; content = badLibrary |} |] |}) |> ok
    Assert.Equal<(string * int * string) list>([ "Library.fs", 2, "FS0001" ], errors r |> List.map where)
    Assert.False(Directory.Exists(Path.Combine(dir, "obj")), "obj/ under the project")
    Assert.False(Directory.Exists(Path.Combine(dir, "bin")), "bin/ under the project")

[<Fact>]
let ``check_project rebuilds a referenced project changed since its last build`` () =
    let root = copyFixtures [ "SampleLib"; "AppWithRef" ]
    let app = Path.Combine(root, "AppWithRef")
    use s = start ()
    Assert.Empty(errors (checkOn s app [||] |> ok))
    File.AppendAllText(Path.Combine(root, "SampleLib", "Library.fs"), "\nlet added = 1\n")
    let r = checkOn s app [| {| path = "Program.fs"; content = "module AppWithRef.Program\nlet doubled = SampleLib.Lib.added * 2\n" |} |] |> ok
    Assert.Empty(errors r)

[<Fact>]
let ``a cancelled check_project does not hold up later calls`` () =
    let dir = copyFixture "SampleLib"
    // Make check_project's MSBuild run take a minute.
    File.WriteAllText(
        Path.Combine(dir, "Directory.Build.targets"),
        """<Project>
  <Target Name="FsiMcpTestSlow" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
    <Exec Condition="'$(OS)' != 'Windows_NT'" Command="sleep 60" />
    <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command Start-Sleep 60" />
  </Target>
</Project>""")
    use s = start ()
    s.Eval "1" |> ok |> ignore // the session is up, so the eval below costs well under a second
    let id = s.Send("tools/call", {| name = "check_project"; arguments = {| project = dir; artifacts_path = Path.Combine(dir, "..", "artifacts") |} |})
    Threading.Thread.Sleep 2000 // inside the MSBuild run
    s.Notify("notifications/cancelled", {| requestId = id; reason = "budget ran out" |})
    let sw = Diagnostics.Stopwatch.StartNew()
    s.Eval "1 + 1" |> ok |> has "val it: int = 2"
    Assert.True(sw.Elapsed < TimeSpan.FromSeconds 10., $"eval waited {sw.Elapsed.TotalSeconds:F1}s behind a cancelled check")
    Server.ToToolResult(s.Await(id, "check_project")) |> failed |> has "cancelled"
