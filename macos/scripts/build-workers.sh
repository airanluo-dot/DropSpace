#!/bin/zsh
set -euo pipefail
ROOT="${0:A:h:h}"
SOURCE="$ROOT/build/worker-sources/llama.cpp"
COMMIT=7fe450e19305b828c199d602c23a8337aaa1f03b
CMAKE="${CMAKE_BIN:-$ROOT/build/tooling/cmake/data/bin/cmake}"
if [[ ! -x "$CMAKE" ]]; then
  print -u2 'CMake is required. Set CMAKE_BIN to an installed CMake 3.25+ executable.'
  exit 1
fi
if [[ ! -d "$SOURCE/.git" ]]; then
  git init -q "$SOURCE"
  git -C "$SOURCE" remote add origin https://github.com/ggml-org/llama.cpp.git
  git -C "$SOURCE" fetch --depth 1 origin "$COMMIT"
  git -C "$SOURCE" checkout --detach FETCH_HEAD
fi
[[ "$(git -C "$SOURCE" rev-parse HEAD)" == "$COMMIT" ]] || { print -u2 'Worker source commit mismatch'; exit 1; }
[[ -z "$(git -C "$SOURCE" status --porcelain)" ]] || { print -u2 'Worker source has unverified changes'; exit 1; }
"$CMAKE" -S "$ROOT/Workers/PlainLyrics" -B "$ROOT/build/workers" -DLLAMA_SOURCE="$SOURCE" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=26.0
"$CMAKE" --build "$ROOT/build/workers" --target plain-lyrics-worker --parallel 2
"$ROOT/build/workers/bin/plain-lyrics-worker" --version
FASTTEXT="$ROOT/build/worker-sources/fastText"
FASTTEXT_COMMIT=1142dc4c4ecbc19cc16eee5cdd28472e689267e6
if [[ ! -d "$FASTTEXT/.git" ]]; then
  git init -q "$FASTTEXT"
  git -C "$FASTTEXT" remote add origin https://github.com/facebookresearch/fastText.git
  git -C "$FASTTEXT" fetch --depth 1 origin "$FASTTEXT_COMMIT"
  git -C "$FASTTEXT" checkout --detach FETCH_HEAD
fi
[[ "$(git -C "$FASTTEXT" rev-parse HEAD)" == "$FASTTEXT_COMMIT" ]] || { print -u2 'fastText source commit mismatch'; exit 1; }
[[ -z "$(git -C "$FASTTEXT" status --porcelain)" ]] || { print -u2 'fastText source has unverified changes'; exit 1; }
"$CMAKE" -S "$ROOT/Workers/FastText" -B "$ROOT/build/language-worker" -DFASTTEXT_SOURCE="$FASTTEXT" -DLLAMA_SOURCE="$SOURCE" -DCMAKE_BUILD_TYPE=Release
"$CMAKE" --build "$ROOT/build/language-worker" --target language-worker --parallel 2
"$ROOT/build/language-worker/bin/language-worker" --version
