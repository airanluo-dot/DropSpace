"""Package actual CUDA producer files without loading a worker or model.

No tests, inference, network requests, publication, or source/version edits occur here.
The small download descriptor must be pinned in the matching App build; the ZIP is
a separate asset of that exact release. This tool does not authenticate a reviewer.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tempfile
import zipfile


REPOSITORY = "airanluo-dot/DropSpace"
RUNTIME_ID = "llama-cpp-v0.5.0-cuda12-win-x64-experiment-v1"
ENGINE_COMMIT = "7fe450e19305b828c199d602c23a8337aaa1f03b"
PROFILE = "hy-q8-plain-resident-v1"
MANIFEST = "cuda-runtime-manifest.json"
DESCRIPTOR = "cuda-runtime-download.json"
COMPONENTS = ("plain-lyrics-worker-cuda.exe", "cublas64_12.dll", "cublasLt64_12.dll")
NOTICES = ("LICENSE-llama.cpp", "LICENSE-CUDA.txt")
HASH = re.compile(r"[a-f0-9]{64}\Z")
COMMIT = re.compile(r"[a-f0-9]{40}\Z")
TAG = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.([1-9][0-9]*))?\Z")
CHUNK = 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def ordinary_path(path, directory=False):
    """Reject symlinks and Windows reparse traversal before opening producer files."""
    path = Path(os.path.abspath(path))
    for ancestor in (path, *path.parents):
        info = ancestor.lstat()
        require(not stat.S_ISLNK(info.st_mode) and
                not getattr(info, "st_file_attributes", 0) & 0x400,
                "CUDA packaging paths must not traverse symlinks/reparse points")
    require(path.is_dir() if directory else path.is_file(), "Expected an ordinary producer path")
    return path


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate CUDA manifest property")
        result[key] = value
    return result


def identity(path, maximum):
    path = ordinary_path(path)
    require(0 < path.stat().st_size <= maximum, "CUDA packaging file exceeds its bound")
    digest = hashlib.sha256()
    count = 0
    with path.open("rb") as source:
        while chunk := source.read(CHUNK):
            count += len(chunk)
            require(count <= maximum, "CUDA packaging file grew beyond its bound")
            digest.update(chunk)
    require(count > 0, "CUDA packaging file became empty")
    return {"name": path.name, "sha256": digest.hexdigest(), "bytes": count}


def read_manifest(payload):
    path = ordinary_path(payload / MANIFEST)
    require(0 < path.stat().st_size <= 16_384, "Invalid CUDA component manifest size")
    with path.open("rb") as source:
        raw = source.read(16_385)
    require(0 < len(raw) <= 16_384, "Invalid CUDA component manifest size")
    require(not raw.startswith(b"\xef\xbb\xbf"), "CUDA component manifest must be UTF-8 without BOM")
    value = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object)
    require(isinstance(value, dict) and type(value.get("schemaVersion")) is int and
            value["schemaVersion"] == 1, "Unsupported CUDA component schema")
    require(value.get("runtimeId") == RUNTIME_ID and value.get("backend") == "cuda" and
            type(value.get("protocol")) is int and value["protocol"] == 1 and value.get("profile") == PROFILE and
            value.get("sourceRepository") == "https://github.com/ggml-org/llama.cpp" and
            value.get("sourceCommit") == ENGINE_COMMIT and
            HASH.fullmatch(value.get("workerSourceSha256", "")), "CUDA component provenance mismatch")
    files = value.get("files")
    require(isinstance(files, list) and len(files) == len(COMPONENTS), "Unexpected CUDA component count")
    total = 0
    for name, entry in zip(COMPONENTS, files):
        require(isinstance(entry, dict) and entry.get("name") == name and
                isinstance(entry.get("sha256"), str) and HASH.fullmatch(entry["sha256"]) and
                type(entry.get("bytes")) is int and 0 < entry["bytes"] <= (805_306_368 if name == "cublasLt64_12.dll" else 536_870_912),
                "Invalid CUDA file integrity record")
        total += entry["bytes"]
    require(total <= 1_073_741_824, "CUDA component payload exceeds its bound")
    return value, {"name": MANIFEST, "sha256": hashlib.sha256(raw).hexdigest(), "bytes": len(raw)}, raw


def write_member(archive, path, expected):
    path = ordinary_path(path)
    entry = zipfile.ZipInfo(expected["name"], date_time=(1980, 1, 1, 0, 0, 0))
    entry.create_system = 3
    entry.external_attr = (stat.S_IFREG | 0o644) << 16
    entry.compress_type = zipfile.ZIP_DEFLATED
    digest = hashlib.sha256()
    count = 0
    with path.open("rb") as source, archive.open(entry, "w") as output:
        while chunk := source.read(CHUNK):
            count += len(chunk)
            require(count <= expected["bytes"], "CUDA producer file changed while packaging")
            digest.update(chunk)
            output.write(chunk)
    require(count == expected["bytes"] and digest.hexdigest() == expected["sha256"],
            "CUDA producer file differs from its pinned integrity record")


def package(args):
    match = TAG.fullmatch(args.app_tag)
    require(match is not None, "Expected an exact stable/Beta App release tag")
    major, minor, patch = (int(match[n]) for n in (1, 2, 3))
    require(major <= 20 and minor <= 99 and patch <= 99 and
            (match[4] is None or int(match[4]) <= 9998), "App tag exceeds the release version range")
    require(COMMIT.fullmatch(args.app_commit) and COMMIT.fullmatch(args.component_commit),
            "Exact App/component source commits are required")
    payload = ordinary_path(args.payload, directory=True)
    notices = ordinary_path(args.license_directory, directory=True)
    require({p.name for p in payload.iterdir()} == {*COMPONENTS, MANIFEST},
            "CUDA payload must contain only its three components and manifest; no models")
    manifest, manifest_identity, manifest_bytes = read_manifest(payload)
    notice_identities = [identity(notices / name, 16 * 1024 * 1024) for name in NOTICES]
    output = Path(os.path.abspath(args.output))
    ordinary_path(output.parent, directory=True)
    require(not os.path.lexists(output), "Output must be fresh; existing artifacts are preserved")
    archive_name = "DropSpace-CUDA-win-x64-" + args.app_tag + ".zip"
    staging = Path(tempfile.mkdtemp(prefix=".dropspace-cuda-package-", dir=output.parent))
    try:
        archive_path = staging / archive_name
        with zipfile.ZipFile(archive_path, "w", compression=zipfile.ZIP_DEFLATED,
                             compresslevel=6, allowZip64=False) as archive:
            for entry in manifest["files"]:
                write_member(archive, payload / entry["name"], entry)
            write_member(archive, payload / MANIFEST, manifest_identity)
            for entry in notice_identities:
                write_member(archive, notices / entry["name"], entry)
        archive_identity = identity(archive_path, 1_200_000_000)
        download = dict(archive_identity, url="https://github.com/" + REPOSITORY +
                        "/releases/download/" + args.app_tag + "/" + archive_name)
        descriptor = {
            "schemaVersion": 1, "repository": REPOSITORY,
            "appRelease": {"tag": args.app_tag, "sourceCommit": args.app_commit},
            "componentSourceCommit": args.component_commit,
            "runtimeId": RUNTIME_ID, "backend": "cuda", "platform": "win-x64",
            "protocol": 1, "profile": PROFILE,
            "engineSourceCommit": ENGINE_COMMIT,
            "workerSourceSha256": manifest["workerSourceSha256"],
            "download": download, "manifest": manifest_identity,
            "files": manifest["files"], "notices": notice_identities,
        }
        descriptor_bytes = (json.dumps(descriptor, ensure_ascii=False, indent=2) + "\n").encode("utf-8")
        require(len(descriptor_bytes) <= 16_384, "Download descriptor exceeds its App resource bound")
        (staging / DESCRIPTOR).write_bytes(descriptor_bytes)
        # Only tiny metadata is copied for the App build. CUDA binary files stay in the ZIP.
        (staging / MANIFEST).write_bytes(manifest_bytes)
        # mkdir fails atomically if another process created the target; never replace
        # an existing artifact directory. Failed publication stays a failed operation.
        output.mkdir()
        for artifact in staging.iterdir():
            artifact.rename(output / artifact.name)
    finally:
        if staging.exists():
            shutil.rmtree(staging)
    print(json.dumps({"output": str(output), "archive": download,
                      "descriptor": DESCRIPTOR, "appRelease": descriptor["appRelease"]}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--license-directory", type=Path, required=True)
    parser.add_argument("--app-tag", required=True)
    parser.add_argument("--app-commit", required=True)
    parser.add_argument("--component-commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    package(parser.parse_args())


if __name__ == "__main__":
    main()
