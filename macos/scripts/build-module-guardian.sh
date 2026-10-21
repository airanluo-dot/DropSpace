#!/bin/zsh
set -euo pipefail
ROOT="${0:A:h:h}"
mkdir -p "$ROOT/build/module-guardian"
xcrun clang -std=c11 -Wall -Wextra -Werror -O2 -arch arm64 -mmacosx-version-min=26.0 \
  "$ROOT/Workers/ModuleGuardian/main.c" -o "$ROOT/build/module-guardian/module-guardian"
codesign --force --sign - "$ROOT/build/module-guardian/module-guardian"
