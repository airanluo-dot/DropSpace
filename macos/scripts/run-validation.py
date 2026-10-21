#!/usr/bin/env python3
"""Launch a native bundle against an explicit isolated profile; never kill user apps."""
import argparse
import os
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--profile', type=Path, required=True)
parser.add_argument('--skip-build', action='store_true')
args = parser.parse_args()
profile = args.profile.resolve()
if not (profile / 'settings.json').is_file():
    raise SystemExit('Prepare an isolated settings.json before launching a validation profile.')
if subprocess.run(['/usr/bin/pgrep', '-x', 'DropSpace'], stdout=subprocess.DEVNULL).returncode == 0:
    raise SystemExit('DropSpace is already running. Quit the owned validation instance before relaunching; no process was stopped.')
if not args.skip_build:
    subprocess.run([str(root / 'scripts/build.sh')], check=True, env={**os.environ, 'CONFIGURATION': 'Debug'})
app = root / 'build/Build/Products/Debug/DropSpace.app'
subprocess.run(['/usr/bin/codesign', '--verify', '--deep', '--strict', str(app)], check=True)
subprocess.run(['/usr/bin/open', '-n', '--env', 'DROPSPACE_DATA_DIRECTORY=' + str(profile), str(app)], check=True)
print('Launched native app with isolated profile:', profile)
