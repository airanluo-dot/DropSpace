"""Source-owned identity of the independently versioned CUDA resource release.

These inputs participate in the AI release source fingerprint. Changing a component
requires new review; an App version alone never creates a new CUDA archive identity.
"""
import re

REPOSITORY = "airanluo-dot/DropSpace"
RUNTIME_ID = "llama-cpp-v0.5.0-cuda13-win-x64-v1"
COMPONENT_TAG = "cuda-" + RUNTIME_ID
ARCHIVE_NAME = "DropSpace-CUDA-" + RUNTIME_ID + ".zip"
ARCHIVE_URL = "https://github.com/" + REPOSITORY + "/releases/download/" + COMPONENT_TAG + "/" + ARCHIVE_NAME
ARCHIVE_SHA256 = "79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b"
ARCHIVE_BYTES = 540873572
MANIFEST_SHA256 = "da8742d806541edf452061eec408f645be704445952a93895bc8e9d6a200215a"
MANIFEST_BYTES = 1815
COMPONENT_COMMIT = "806f3e3e40c11a6e7d3d50648a9de8708b16b4ac"
ENGINE_COMMIT = "7fe450e19305b828c199d602c23a8337aaa1f03b"
PROFILE = "hy-q8-plain-resident-v1"
MANIFEST = "cuda-runtime-manifest.json"
DESCRIPTOR = "cuda-runtime-download.json"
COMPONENT_DESCRIPTOR = "cuda-runtime-component.json"
COMPONENTS = ("plain-lyrics-worker-cuda.exe", "cublas64_13.dll", "cublasLt64_13.dll")
NOTICES = ("LICENSE-llama.cpp", "LICENSE-CUDA.txt")
HASH = re.compile(r"[a-f0-9]{64}\Z")
COMMIT = re.compile(r"[a-f0-9]{40}\Z")
TAG = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.([1-9][0-9]*))?\Z")


def require(condition, message):
    if not condition:
        raise ValueError(message)


def component_descriptor(manifest, manifest_identity, archive, notices, component_commit):
    require(component_commit == COMPONENT_COMMIT, "CUDA component source requires a new reviewed identity")
    require(manifest["runtimeId"] == RUNTIME_ID and manifest["backend"] == "cuda" and
            type(manifest["protocol"]) is int and manifest["protocol"] == 1 and
            manifest["profile"] == PROFILE and manifest["sourceCommit"] == ENGINE_COMMIT,
            "CUDA component ABI/profile/engine mismatch")
    require(archive["name"] == ARCHIVE_NAME and HASH.fullmatch(archive["sha256"]) and
            type(archive["bytes"]) is int and 0 < archive["bytes"] <= 1_073_741_824,
            "Invalid independent CUDA archive identity")
    require(archive["sha256"] == ARCHIVE_SHA256 and archive["bytes"] == ARCHIVE_BYTES and
            manifest_identity["name"] == MANIFEST and
            manifest_identity["sha256"] == MANIFEST_SHA256 and manifest_identity["bytes"] == MANIFEST_BYTES,
            "Changed CUDA bytes require a new reviewed component identity; never overwrite an existing release")
    return {
        "schemaVersion": 2, "repository": REPOSITORY,
        "componentRelease": {"tag": COMPONENT_TAG},
        "componentSourceCommit": component_commit,
        "runtimeId": RUNTIME_ID, "backend": "cuda", "platform": "win-x64",
        "protocol": 1, "profile": PROFILE,
        "engineSourceCommit": ENGINE_COMMIT,
        "workerSourceSha256": manifest["workerSourceSha256"],
        "download": dict(archive, url=ARCHIVE_URL), "manifest": manifest_identity,
        "files": manifest["files"], "notices": notices,
    }
