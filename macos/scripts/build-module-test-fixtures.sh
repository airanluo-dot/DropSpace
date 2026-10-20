#!/bin/zsh
set -euo pipefail
ROOT=${0:A:h:h}
mkdir -p "$ROOT/build/module-guardian"
xcrun clang -arch arm64 -mmacosx-version-min=26.0 -O2 -Wall -Wextra -Werror \
  "$ROOT/Tests/DropSpaceInfrastructureTests/Fixtures/module-fault-worker.c" -o "$ROOT/build/module-guardian/module-fault-worker"
xcrun clang -arch arm64 -mmacosx-version-min=26.0 -O2 -fobjc-arc -Wall -Wextra -Werror -framework Foundation \
  "$ROOT/Tests/DropSpaceInfrastructureTests/Fixtures/module-lifecycle-worker.m" -o "$ROOT/build/module-guardian/module-lifecycle-worker"
for executable in module-fault-worker module-lifecycle-worker; do
  codesign --force --sign - --timestamp=none "$ROOT/build/module-guardian/$executable"
done
