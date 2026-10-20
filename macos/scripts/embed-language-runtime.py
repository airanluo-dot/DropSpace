#!/usr/bin/env python3
"""Embed fixed native language/translation runtimes before the app is signed."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--app', type=Path, required=True)
args = parser.parse_args()
manifest = json.loads((root.parent / 'src/DropSpace.Infrastructure/Lyrics/Manifests/fasttext-lid176-bin-v1.json').read_text())
model = root / 'build/ValidationModels/lid.176.bin'
worker = root / 'build/language-worker/bin/language-worker'

def verify_model(path):
    if not path.is_file() or path.is_symlink() or path.stat().st_size != manifest['bytes']:
        raise SystemExit('Required bundled language model is missing or has an invalid size. Run macos/scripts/build.sh.')
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b''): digest.update(chunk)
    if digest.hexdigest() != manifest['sha256']:
        raise SystemExit('Bundled language model hash mismatch; no substitute model is permitted.')

verify_model(model)
if not worker.is_file() or worker.is_symlink():
    raise SystemExit('Required language worker is missing. Run macos/scripts/build.sh.')
identity = json.loads(subprocess.check_output([str(worker), '--version'], text=True))
if identity != {'protocol': 1, 'engineCommit': manifest['engineCommit'], 'modelSha256': manifest['sha256'], 'platform': 'macos', 'architecture': 'arm64'}:
    raise SystemExit('Language worker identity does not match the frozen manifest.')
helpers = args.app / 'Contents/Helpers'
models = args.app / 'Contents/Resources/LanguageModels'
licenses = args.app / 'Contents/Resources/ThirdPartyLicenses'
for directory in [helpers, models, licenses]: directory.mkdir(parents=True, exist_ok=True)
subprocess.run(['/usr/bin/python3', str(root / 'scripts/embed-branding.py'), '--app', str(args.app)], check=True)
subprocess.run(['/bin/zsh', str(root / 'scripts/build-module-guardian.sh')], check=True)
guardian = root / 'build/module-guardian/module-guardian'
shutil.copy2(guardian, helpers / guardian.name)
(helpers / guardian.name).chmod(0o755)
subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', str(helpers / guardian.name)], check=True)
shutil.copy2(worker, helpers / worker.name)
(helpers / worker.name).chmod(0o755)
subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', str(helpers / worker.name)], check=True)
shutil.copy2(model, models / model.name)
verify_model(models / model.name)
shutil.copy2(root / 'build/worker-sources/fastText/LICENSE', licenses / 'fastText-LICENSE.txt')
shutil.copy2(root / 'docs/THIRD-PARTY.md', licenses / 'Attribution.md')
translation = root / 'build/workers/bin/plain-lyrics-worker'
if not translation.is_file() or translation.is_symlink():
    raise SystemExit('Required translation worker is missing. Run macos/scripts/build.sh.')
version = json.loads(subprocess.check_output([str(translation), '--version'], text=True))
if version != {'protocol': 1, 'profile': 'hy-q8-plain-resident-v1', 'engineCommit': '7fe450e19305b828c199d602c23a8337aaa1f03b', 'platform': 'macos', 'architecture': 'arm64'}:
    raise SystemExit('Translation worker identity mismatch. Rebuild the fixed native worker.')
shutil.copy2(translation, helpers / translation.name)
(helpers / translation.name).chmod(0o755)
subprocess.run(['/usr/bin/codesign', '--force', '--sign', '-', '--timestamp=none', str(helpers / translation.name)], check=True)
shutil.copy2(root / 'build/worker-sources/llama.cpp/LICENSE', licenses / 'llama.cpp-LICENSE.txt')
print('Bundled unchanged language model and ad-hoc signed arm64 worker verified.')
