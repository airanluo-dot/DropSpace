#!/bin/zsh
set -euo pipefail
ROOT="${0:A:h:h}"
"$ROOT/scripts/build-workers.sh"
python3 "$ROOT/scripts/fetch-validation-language-model.py"
python3 "$ROOT/scripts/generate-project.py"
xcodebuild -project "$ROOT/DropSpace.xcodeproj" -scheme DropSpace -configuration "${CONFIGURATION:-Debug}" -destination 'platform=macOS,arch=arm64' -derivedDataPath "$ROOT/build" ARCHS=arm64 ONLY_ACTIVE_ARCH=YES build
