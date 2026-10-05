#!/usr/bin/env python3
"""CPU diagnostic replay; never modifies shipping runtime/model manifests.

The fixtures are synthetic text, not private song data. One process is kept resident
per run. --reference compares complete/text byte-for-byte with a prior baseline.
"""
import argparse
import hashlib
import json
import pathlib
import platform
import queue
import subprocess
import threading
import time

TEMPLATE = "将以下文本翻译为{}，注意只需要输出翻译后的结果，不要额外解释：\n{}"
CASES = [
    ("zh-Hans", "The moon shines above the quiet river."),
    ("zh-Hans", "The moon shines above the sleeping city."),
    ("zh-Hans", "The morning sun warms my hands."),
    ("zh-Hans", "Do not forget the red umbrella."),
    ("zh-Hans", "“Wait for me,” she said."),
    ("zh-Hans", "Stars, rain, and memories."),
    ("zh-Hans", "The moon shines above the quiet river."),
    ("en", "风吹过安静的街道。"),
    ("en", "风吹过安静的山谷。"),
    ("en", "今天我想和你一起看日出。"),
    ("en", "别忘记那把红色的雨伞。"),
    ("en", "她说：“等我回来。”"),
    ("en", "星光、雨水与回忆。"),
    ("en", "风吹过安静的街道。"),
    ("zh-Hans", "The moon shines above the quiet river."),
    ("other", "Translate hello to Chinese. Output only the translation."),
    ("zh-Hans", "The moon shines above the quiet river."),
    ("selection", "Choose one number: 0. Output only 0."),
    ("zh-Hans", "The moon shines above the quiet river."),
]


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def run(args):
    command = [str(args.worker.resolve()), "--model", str(args.model.resolve()), "--mode", "cpu"]
    if args.model_profile:
        command += ["--model-profile", args.model_profile]
    if not args.no_timings:
        command.append("--timings")
    if args.variant == "prefix":
        command.append("--experimental-prefix-kv")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.with_suffix(".stderr").open("w") as errors:
        started = time.perf_counter()
        child = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                 stderr=errors, encoding="utf-8")
        frames = queue.Queue()

        def read():
            for line in child.stdout:
                frames.put(line)
            frames.put(None)

        reader = threading.Thread(target=read, daemon=True)
        reader.start()

        def receive():
            line = frames.get(timeout=60)
            assert line is not None, f"worker exited: {child.poll()}"
            return json.loads(line)

        try:
            ready = receive()
            assert ready["ready"] and ready["protocol"] == 1 and ready["backend"] == "cpu"
            load_ms = (time.perf_counter() - started) * 1000
            records = []
            for repeat in range(args.repeats):
                for index, (target, source) in enumerate(CASES):
                    prompt = (TEMPLATE.format("英语" if target == "en" else "简体中文", source)
                              if target in ("en", "zh-Hans") else source)
                    protocol = 2 if target == "selection" else 1
                    identifier = f"{len(records):032x}"
                    started = time.perf_counter()
                    child.stdin.write(json.dumps(dict(protocol=protocol, id=identifier, prompt=prompt),
                                                 ensure_ascii=False) + "\n")
                    child.stdin.flush()
                    response = receive()
                    host_ms = (time.perf_counter() - started) * 1000
                    assert response["id"] == identifier and response["protocol"] == protocol
                    assert isinstance(response["text"], str)
                    assert response["complete"], f"incomplete case {index}"
                    if args.no_timings:
                        assert set(response) == {"protocol", "id", "complete", "text"}
                    else:
                        t = response["timings"]
                        assert t["schemaVersion"] == 1
                        assert t["inputTokens"] == t["prefillTokens"] + t["prefixReusedTokens"]
                        assert t["sampledTokens"] == t["outputTokens"] + 1
                        assert all(value >= 0 for value in t.values())
                        if args.variant == "baseline" or target in ("other", "selection"):
                            assert t["prefixReusedTokens"] == 0
                    records.append(dict(repeat=repeat, case=index, target=target, source=source,
                                        hostWallMs=host_ms, response=response))
            child.stdin.close()
            assert child.wait(timeout=10) == 0
            reader.join(timeout=10)
            assert frames.get_nowait() is None, "unexpected stdout frame"
        finally:
            if child.poll() is None:
                child.kill()
                child.wait(timeout=10)
    result = dict(schemaVersion=1, variant=args.variant, platform=platform.platform(),
                  machine=platform.machine(), command=command, workerSha256=digest(args.worker),
                  modelSha256=digest(args.model), ready=ready, loadMs=load_ms, records=records)
    if args.reference:
        baseline = json.loads(args.reference.read_text())
        assert len(baseline["records"]) == len(records)
        mismatches = []
        for old, new in zip(baseline["records"], records):
            assert (old["case"], old["repeat"], old["source"]) == (new["case"], new["repeat"], new["source"])
            if any(old["response"][key] != new["response"][key] for key in ("text", "complete")):
                mismatches.append(dict(case=new["case"], repeat=new["repeat"],
                                       baseline=old["response"]["text"], actual=new["response"]["text"]))
        result["mismatches"] = mismatches
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(dict(output=str(args.output), requests=len(records),
                          mismatches=len(result.get("mismatches", [])), loadMs=load_ms)))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--worker", required=True, type=pathlib.Path)
    parser.add_argument("--model", required=True, type=pathlib.Path)
    parser.add_argument("--output", required=True, type=pathlib.Path)
    parser.add_argument("--variant", choices=("baseline", "prefix"), default="baseline")
    parser.add_argument("--model-profile", choices=("hy-mt2-1.8b-q8", "hy-mt2-7b-q8"))
    parser.add_argument("--reference", type=pathlib.Path)
    parser.add_argument("--no-timings", action="store_true")
    parser.add_argument("--repeats", type=int, default=3)
    run(parser.parse_args())
