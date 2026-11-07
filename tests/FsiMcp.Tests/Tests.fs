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
    Assert.Equal<string list>([ "eval"; "typecheck"; "reset"; "bindings"; "profile"; "load_project"; "check_project" ], names)

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
