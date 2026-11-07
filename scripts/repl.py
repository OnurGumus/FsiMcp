#!/usr/bin/env python3
"""Keep ONE fsi-mcp session alive across shell commands, so an agent can use it step by step.

  repl.py serve <server-dll> <socket> [cwd]     # run in the background; owns the MCP session
                                               # (keep <socket> short: macOS caps socket paths at 104 chars)
  repl.py <socket> eval|typecheck [timeout] < code
  repl.py <socket> load_project <path> [build] [configuration]
  repl.py <socket> reset
"""
import json, os, socket, subprocess, sys, time

def serve(dll, sock_path, cwd):
    proc = subprocess.Popen(["dotnet", dll], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                            stderr=subprocess.DEVNULL, cwd=cwd, text=True, bufsize=1)
    ids = iter(range(1, 10**9))
    def rpc(method, params=None, notify=False):
        msg = {"jsonrpc": "2.0", "method": method, **({"params": params} if params is not None else {})}
        if not notify:
            msg["id"] = next(ids)
        proc.stdin.write(json.dumps(msg) + "\n"); proc.stdin.flush()
        if notify:
            return None
        while True:
            resp = json.loads(proc.stdout.readline())
            if resp.get("id") == msg["id"]:
                return resp
    rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "repl", "version": "0"}})
    rpc("notifications/initialized", notify=True)
    if os.path.exists(sock_path):
        os.unlink(sock_path)
    srv = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    srv.bind(sock_path); srv.listen(1)
    print(f"serving {sock_path}", flush=True)
    log = open(sock_path + ".log", "a")
    while True:
        conn, _ = srv.accept()
        with conn:
            data = b""
            while not data.endswith(b"\n"):
                chunk = conn.recv(65536)
                if not chunk:
                    break
                data += chunk
            req = json.loads(data)
            t = time.time()
            res = rpc("tools/call", {"name": req["tool"], "arguments": req["args"]})["result"]
            ms = 1000 * (time.time() - t)
            log.write(json.dumps({"tool": req["tool"], "args": req["args"], "ms": round(ms), "isError": res.get("isError", False)}) + "\n"); log.flush()
            out = {"text": res["content"][0]["text"], "isError": res.get("isError", False), "ms": ms}
            conn.sendall((json.dumps(out) + "\n").encode())

def call(sock_path, tool, rest):
    if tool in ("eval", "typecheck"):
        args = {"code": sys.stdin.read()}
        if rest:
            args["timeout_seconds"] = int(rest[0])
    elif tool == "load_project":
        args = {"path": rest[0]}
        if len(rest) > 1: args["build"] = rest[1] == "true"
        if len(rest) > 2: args["configuration"] = rest[2]
    else:
        args = {}
    s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    s.connect(sock_path)
    s.sendall((json.dumps({"tool": tool, "args": args}) + "\n").encode())
    data = b""
    while not data.endswith(b"\n"):
        chunk = s.recv(65536)
        if not chunk:
            break
        data += chunk
    out = json.loads(data)
    print(out["text"])
    print(f"[{tool} {out['ms']:.0f} ms{' ERROR' if out['isError'] else ''}]")

if sys.argv[1] == "serve":
    serve(sys.argv[2], sys.argv[3], sys.argv[4] if len(sys.argv) > 4 else None)
else:
    call(sys.argv[1], sys.argv[2], sys.argv[3:])
