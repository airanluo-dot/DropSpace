#!/usr/bin/env python3
"""Package the unchanged canonical artwork in native macOS icon containers."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--app', type=Path, required=True)
args = parser.parse_args()
manifest = json.loads((root.parent / 'branding/SOURCE_MANIFEST.json').read_text())['activeSource']
source = root.parent / 'branding' / manifest['path']
if hashlib.sha256(source.read_bytes()).hexdigest() != manifest['sha256']:
    raise SystemExit('Canonical branding source does not match its recorded hash.')
destination = args.app / 'Contents/Resources'
destination.mkdir(parents=True, exist_ok=True)
shutil.copy2(source, destination / 'DropSpaceLogo.png')
with tempfile.TemporaryDirectory(prefix='dropspace-icon-') as temporary:
    iconset = Path(temporary) / 'DropSpace.iconset'
    iconset.mkdir()
    for size in [16, 32, 128, 256, 512]:
        for scale in [1, 2]:
            name = f'icon_{size}x{size}' + ('@2x' if scale == 2 else '') + '.png'
            subprocess.run(['/usr/bin/sips', '-z', str(size * scale), str(size * scale), str(source), '--out', str(iconset / name)], check=True, stdout=subprocess.DEVNULL)
    subprocess.run(['/usr/bin/iconutil', '-c', 'icns', str(iconset), '-o', str(destination / 'DropSpace.icns')], check=True)
