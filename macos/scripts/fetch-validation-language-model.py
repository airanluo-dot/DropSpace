#!/usr/bin/env python3
"""Fetch the unchanged frozen fastText model into the developer build cache.

The same verified bytes are used for validation and bundled in the application.
This is not a runtime component installer and never writes user application data.
"""
import hashlib
import json
import os
from pathlib import Path
import urllib.request

root = Path(__file__).resolve().parents[2]
manifest = json.loads((root / 'src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json').read_text())
assert manifest['bytes'] == 131266198
assert manifest['sha256'] == '7e69ec5451bc261cc7844e49e4792a85d7f09c06789ec800fc4a44aec362764e'
assert manifest['source'] == 'https://dl.fbaipublicfiles.com/fasttext/supervised-models/lid.176.bin'
folder = root / 'macos/build/ValidationModels'
folder.mkdir(parents=True, exist_ok=True)
destination = folder / manifest['modelFile']

def verify(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as file:
        for chunk in iter(lambda: file.read(1024 * 1024), b''): digest.update(chunk)
    return path.stat().st_size == manifest['bytes'] and digest.hexdigest() == manifest['sha256']

if destination.exists():
    if destination.is_symlink() or not verify(destination): raise RuntimeError('Existing validation model is invalid; preserved for inspection')
    print('Existing model verified:', destination)
else:
    temporary = destination.with_suffix('.bin.downloading')
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(fd, 'wb') as output, urllib.request.urlopen(manifest['source'], timeout=30) as response:
            total = 0
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk: break
                total += len(chunk)
                if total > manifest['bytes']: raise RuntimeError('Model exceeds pinned size')
                output.write(chunk)
            output.flush(); os.fsync(output.fileno())
        if not verify(temporary): raise RuntimeError('Model byte/hash mismatch')
        # Hard-link publication refuses an existing destination; no user file is replaced.
        os.link(temporary, destination)
        print('Verified original model:', total, manifest['sha256'], destination)
    finally:
        temporary.unlink(missing_ok=True)
