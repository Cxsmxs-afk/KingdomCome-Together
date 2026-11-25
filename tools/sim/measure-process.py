#!/usr/bin/env python3
# Copyright (C) 2026 the Kingdom Come: Together contributors (AUTHORS). SPDX-License-Identifier: GPL-3.0-only
"""Measure the production Agent and Relay executables with local engine doubles.

Linux, .NET 8. Build the Agent and Relay in Release before running this bench.
Usage: python3 tools/sim/measure-process.py CHECKOUT OUTPUT [SECONDS]
"""
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import threading
import time
from urllib.parse import unquote, urlsplit

revision = Path(sys.argv[1]).resolve()
out = Path(sys.argv[2]).resolve()
seconds = float(sys.argv[3]) if len(sys.argv) > 3 else 60
level = "Release"
assemblies = {role: revision / f"dotnet/KcdMp.{project}/bin/{level}/net8.0/KcdMp{project}.dll"
              for role, project in [("relay", "Server"), ("agent", "Client")]}


class Engine(BaseHTTPRequestHandler):
    def do_GET(self):
        if "Calendar" in self.path:
            payload = f'<Calendar GameTime="{self.server.game_time}" />'
        elif "Cvar" in self.path:
            payload = '<value>kcdmp-where=world</value>'
        else:
            payload = "<ok />"
        command = unquote(urlsplit(self.path).query)
        for name, token in re.findall(r'(KCD2MP_\w+)\("([a-f0-9]{8})"(?:\)|,)', command):
            if name == "KCD2MP_W147Hostiles":
                self.server.event("wo124_reply", f"{token} drawn=0 on=1 n=0 list=-")
            elif name == "KCD2MP_S3ReadVitals":
                self.server.event("wo124_reply", f"{token} unavailable")
        data = payload.encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        size = int(self.headers.get("Content-Length", "0"))
        self.rfile.read(size)
        self.do_GET()

    def log_message(self, *_):
        pass


class EngineServer(ThreadingHTTPServer):
    def __init__(self, log, active):
        super().__init__(("127.0.0.1", 0), Engine)
        self.game_time = 10 if active else 0
        self.log = log
        self.sequence = 0
        self.log_lock = threading.Lock()

    def event(self, name, value):
        with self.log_lock, self.log.open("a") as writer:
            self.sequence += 1
            writer.write(f"[KCD2-MP-EVT] v1 {self.sequence} {name} {value}\n")


def local_port():
    with socket.socket() as server:
        server.bind(("127.0.0.1", 0))
        return server.getsockname()[1]


def ticks(pid):
    fields = Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    return int(fields[11]) + int(fields[12])


def stop_process(process):
    if process.poll() is None:
        process.terminate()
    try:
        process.wait(timeout=7)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait()


def produce(logs, closed):
    with ExitStack() as stack:
        writers = [stack.enter_context(log.open("a", buffering=1)) for log in logs]
        until = time.monotonic()
        seq = 0
        while not closed.is_set():
            seq += 1
            for i, writer in enumerate(writers):
                writer.write(f"[KCD2-MP-DATA] v2 {seq} {10 + seq * .05:.2f} {100 + i * 4 + (seq % 40) * .04:.2f} 100 10 0 0 100 80\n")
            until += .05
            closed.wait(max(0, until - time.monotonic()))


def measure(players, active):
    run = out / ("four" if active else "idle")
    run.mkdir(parents=True, exist_ok=True)
    port, http = local_port(), local_port()
    with ExitStack() as stack:
        def launch(role, folder, opts, env=None):
            folder.mkdir(exist_ok=True)
            log = stack.enter_context((folder / f"{role}.log").open("wb"))
            dll = assemblies[role]
            if role == "agent":
                # The Agent mirrors logs next to its assembly. Give each
                # player its own installation so that log I/O stays local.
                application = folder / "app"
                shutil.copytree(dll.parent, application, dirs_exist_ok=True,
                                ignore=shutil.ignore_patterns("*.log", "*.jsonl", "*.whs"))
                dll = application / dll.name
            proc = subprocess.Popen(["dotnet", str(dll), *opts], cwd=folder, stdout=log,
                                    stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL, env=env)
            stack.callback(stop_process, proc)
            return proc

        relay_dir = run / "relay"
        relay = launch("relay", relay_dir,
                       ["--port", str(port), "--Tcp:BindAddress", "127.0.0.1",
                        "--Urls", f"http://127.0.0.1:{http}", "--Serilog:WriteTo:1:Name", "Console"])
        until = time.monotonic() + 10
        while not (relay_dir / "relay.log").exists() or "Listening" not in (relay_dir / "relay.log").read_text():
            if relay.poll() is not None or time.monotonic() >= until:
                raise RuntimeError(f"Relay failed to start: {relay_dir}")
            time.sleep(.05)
        agents, logs = [], []
        for i in range(players):
            folder = run / f"player-{i}"
            folder.mkdir(exist_ok=True)
            log = folder / "kcd.log"
            log.write_text("=== MOD INIT ===\n")
            logs.append(log)
            engine = EngineServer(log, active)
            threading.Thread(target=engine.serve_forever, daemon=True).start()
            stack.callback(engine.server_close)
            stack.callback(engine.shutdown)
            opts = ["--port", str(port), "--host", "127.0.0.1", "--name", f"bench-player-{i}",
                    "--game-api", f"http://127.0.0.1:{engine.server_port}", "--transport", "logtail",
                    "--no-voice", "--no-discord", "--no-pos-native", "--no-npc-scan-native", "--version-ipc-port", str(local_port()), "--dice-ipc-port", str(local_port())]
            if i == 0 and active:
                opts.append("--hosting")
            env = dict(os.environ, KCDMP_INSTANCE=f"review74-{i}", KCD2MP_INSTALL=str(folder), KCDMP_DATA_DIR=str(folder / "local-data"))
            agents.append(launch("agent", folder, opts, env))

        if active:
            closed = threading.Event()
            thread = threading.Thread(target=produce, args=(logs, closed), daemon=True)
            thread.start()
            stack.callback(thread.join)
            stack.callback(closed.set)
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline:
            if all("MP-LINK hello answered:" in (run / f"player-{i}" / "agent.log").read_text(errors="replace")
                   for i in range(players)):
                break
            if relay.poll() is not None or any(agent.poll() is not None for agent in agents):
                raise RuntimeError(f"Process exited during setup in {run}")
            time.sleep(.2)
        else:
            raise RuntimeError(f"Scheduler handshake missing in {run}")
        time.sleep(10)
        procs = [relay, *agents]
        start = time.monotonic()
        before = [ticks(proc.pid) for proc in procs]
        time.sleep(seconds)
        after = [ticks(proc.pid) for proc in procs]
        duration = time.monotonic() - start
        if any(proc.poll() is not None for proc in procs):
            raise RuntimeError(f"Process exited during measurement in {run}")
        cpu = [round((b - a) * 60 / duration / os.sysconf("SC_CLK_TCK"), 3) for a, b in zip(before, after)]
        for agent in agents:
            stop_process(agent)
        pose_frames = []
        for i in range(players):
            agent_log = (run / f"player-{i}" / "agent.log").read_text(errors="replace")
            summary = re.search(r"MP-LINK kinds-total .*? out=(.*?) in=", agent_log)
            match = re.search(r"(?:^|,)pos:(\d+)/", summary[1]) if summary else None
            pose_frames.append(int(match[1]) if match else 0)
        if active and any(count == 0 for count in pose_frames):
            raise RuntimeError(f"Moving agents sent too few poses: {pose_frames}; see {run}")
        return {"players": players, "moving": active, "wall_s": duration, "relay_cpu_s_per_min": cpu[0],
                "agent_cpu_s_per_min": cpu[1:], "agent_total_cpu_s_per_min": round(sum(cpu[1:]), 3),
                "agent_position_frames": pose_frames}


for args in [(1, False), (4, True)]:
    print(json.dumps(measure(*args)), flush=True)
