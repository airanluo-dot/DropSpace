"""Bind reviewed independent CUDA metadata to one exact App checkout.

No runtime/model downloads, compilation, executable loading or inference occurs.
Only the tiny App descriptor changes between App releases; the ZIP/manifest/cache
identity is reused until a new component is reviewed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

from cuda_runtime_contract import (
    ARCHIVE_BYTES, ARCHIVE_NAME, ARCHIVE_SHA256, COMMIT, COMPONENT_COMMIT,
    DESCRIPTOR, MANIFEST, MANIFEST_BYTES, MANIFEST_SHA256, TAG,
    component_descriptor, require,
)


def stage(root, app_commit):
    require(COMMIT.fullmatch(app_commit), 'Exact final App source SHA required')
    head = subprocess.check_output(['git', '-C', str(root), 'rev-parse', 'HEAD'], text=True).strip()
    require(head == app_commit, 'Build metadata must match the actual checkout')
    tag = (root / 'RELEASE_VERSION').read_text(encoding='utf-8').strip()
    match = TAG.fullmatch(tag)
    require(match is not None and int(match[1]) <= 20 and int(match[2]) <= 99 and
            int(match[3]) <= 99 and (match[4] is None or int(match[4]) <= 9998),
            'Expected an exact supported App release tag')
    evidence = root / 'docs/dev/evidence/beta11-local-cuda'
    report = json.loads((evidence / 'cuda13-producer-report.json').read_text(encoding='utf-8'))
    raw = (evidence / 'cuda13-runtime-manifest.json').read_bytes()
    manifest_identity = report['manifest']
    # Git normalizes text evidence on non-Windows checkouts. Restore the exact
    # producer CRLF bytes before checking the immutable component/cache identity.
    if hashlib.sha256(raw).hexdigest() != manifest_identity['sha256']:
        raw = raw.replace(b'\r\n', b'\n').replace(b'\n', b'\r\n')
    require(len(raw) == manifest_identity['bytes'] == MANIFEST_BYTES and
            hashlib.sha256(raw).hexdigest() == manifest_identity['sha256'] ==
            MANIFEST_SHA256,
            'Reviewed CUDA manifest bytes changed')
    inner = json.loads(raw)
    require(inner['runtimeId'] == report['runtimeId'] and
            inner['sourceCommit'] == report['engineSourceCommit'] and
            inner['workerSourceSha256'] == report['workerSourceSha256'] and
            inner['files'] == report['files'] and
            report['componentSourceCommit'] == COMPONENT_COMMIT,
            'Reviewed CUDA producer provenance changed')
    archive = dict(report['archive'])
    require(archive['name'] == 'DropSpace-CUDA-win-x64-v0.3.1-beta.11.zip' and
            archive['bytes'] == ARCHIVE_BYTES and archive['sha256'] ==
            ARCHIVE_SHA256,
            'Reviewed CUDA archive bytes changed')
    # Initial standalone publication renames the verified existing ZIP, never repacks it.
    archive['name'] = ARCHIVE_NAME
    outer = component_descriptor(inner, manifest_identity, archive, report['notices'], COMPONENT_COMMIT)
    outer['appRelease'] = {'tag': tag, 'sourceCommit': head}
    data = (json.dumps(outer, ensure_ascii=False, indent=2) + '\n').encode('utf-8')
    require(len(data) <= 16384, 'App metadata exceeds its resource bound')
    destination = root / 'artifacts/cuda-runtime/win-x64'
    require(not os.path.lexists(destination), 'Metadata destination must be fresh; existing files are preserved')
    destination.mkdir(parents=True)
    (destination / MANIFEST).write_bytes(raw)
    (destination / DESCRIPTOR).write_bytes(data)
    return {'appRelease': outer['appRelease'], 'componentRelease': outer['componentRelease'],
            'archive': outer['download'], 'metadataDirectory': str(destination),
            'nativeRebuilt': False, 'testsRun': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--app-commit', required=True)
    args = parser.parse_args()
    print(json.dumps(stage(Path(__file__).resolve().parent.parent, args.app_commit), indent=2))


if __name__ == '__main__':
    main()
