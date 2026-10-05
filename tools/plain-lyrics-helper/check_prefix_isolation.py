#!/usr/bin/env python3
"""Real CPU worker cancellation/EOF/restart checks; not Windows Job validation."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading
import time

from benchmark_prefix import TEMPLATE


class Worker:
    def __init__(self, command):
        self.frames = queue.Queue()
        self.child = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                      stderr=subprocess.DEVNULL, encoding="utf-8")

        def read():
            for line in self.child.stdout:
                self.frames.put(json.loads(line))
            self.frames.put(None)

        self.reader = threading.Thread(target=read, daemon=True)
        self.reader.start()
        assert self.receive()["experimentalPrefixKv"] is True

    def receive(self):
        response = self.frames.get(timeout=60)
        assert response is not None, f"worker exited: {self.child.poll()}"
        return response

    def request(self, source):
        request = dict(protocol=1, id="0" * 32, prompt=TEMPLATE.format("简体中文", source))
        self.child.stdin.write(json.dumps(request, ensure_ascii=False) + "\n")
        self.child.stdin.flush()

    def reap(self, kill):
        started = time.perf_counter()
        if kill:
            self.child.kill()
        else:
            self.child.stdin.close()
        code = self.child.wait(timeout=10)
        self.reader.join(timeout=10)
        assert not self.reader.is_alive()
        assert self.frames.get_nowait() is None, "unexpected result after cancellation/EOF"
        return dict(exitCode=code, reapMs=(time.perf_counter() - started) * 1000)


def run(args):
    command = [str(args.worker.resolve()), "--model", str(args.model.resolve()), "--mode", "cpu",
               "--timings", "--experimental-prefix-kv"]
    source = "The moon shines above the quiet river."
    checks = []
    for delay in (0.05, 0.25):
        owner = Worker(command)
        try:
            owner.request(source)
            first = owner.receive()
            assert first["complete"] and first["timings"]["prefixReusedTokens"] == 0
            owner.request("The wind carries a thousand stories across the silent ocean. " * 12)
            try:
                owner.frames.get(timeout=delay)
                raise AssertionError("request completed before intended in-flight cancellation")
            except queue.Empty:
                pass
            checks.append(dict(kind="in-flight-kill", delayMs=delay * 1000, **owner.reap(True)))
        finally:
            if owner.child.poll() is None:
                owner.child.kill()
                owner.child.wait(timeout=10)
        replacement = Worker(command)
        try:
            replacement.request(source)
            after = replacement.receive()
            assert after["complete"] and after["text"] == first["text"]
            assert after["timings"]["prefixReusedTokens"] == 0
            replacement.request(source)
            warm = replacement.receive()
            assert warm["text"] == first["text"] and warm["timings"]["prefixReusedTokens"] > 0
            checks.append(dict(kind="fresh-owner-after-kill", coldReusedTokens=0,
                               warmReusedTokens=warm["timings"]["prefixReusedTokens"],
                               **replacement.reap(False)))
        finally:
            if replacement.child.poll() is None:
                replacement.child.kill()
                replacement.child.wait(timeout=10)
    replacement = Worker(command)
    try:
        replacement.request(source)
        result = replacement.receive()
        assert result["text"] == first["text"] and result["timings"]["prefixReusedTokens"] == 0
        checks.append(dict(kind="fresh-owner-after-graceful-eof", coldReusedTokens=0,
                           **replacement.reap(False)))
    finally:
        if replacement.child.poll() is None:
            replacement.child.kill()
            replacement.child.wait(timeout=10)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(dict(schemaVersion=1, platform="Linux CPU process ownership",
                                          checks=checks), indent=2) + "\n")
    print(json.dumps(dict(output=str(args.output), checks=len(checks))))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", type=pathlib.Path, required=True)
    parser.add_argument("--model", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    run(parser.parse_args())
