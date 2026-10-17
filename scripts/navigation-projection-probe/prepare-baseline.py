"""Extract unchanged navigation methods from a Git revision for the same probe fixture."""

import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
revision = sys.argv[1] if len(sys.argv) > 1 else "6604f59"
commit = subprocess.check_output(["git", "rev-parse", revision], cwd=ROOT, text=True).strip()
source_path = "src/DropSpace.App/ViewModels/MainViewModel.cs"
source_bytes = subprocess.check_output(["git", "show", f"{commit}:{source_path}"], cwd=ROOT)
source = source_bytes.decode("utf-8")
markers = [
    ("    public async Task NavigateAsync(", "    public async Task<int> AddPathsAsync("),
    ("    private void ClearMainProjection()", "    private bool RemoveMainProjectionCard("),
    ("    private sealed class ProjectionLoadJournal", "    private void TrimLiveClipboardProjection("),
]
chunks = []
segments = []
for start_marker, end_marker in markers:
    start = source.index(start_marker)
    end = source.index(end_marker, start)
    chunk = source[start:end]
    chunks.append(chunk)
    segments.append({
        "start_marker": start_marker,
        "end_marker": end_marker,
        "first_line": source[:start].count("\n") + 1,
        "sha256": hashlib.sha256(chunk.encode("utf-8")).hexdigest(),
    })
output = ROOT / "artifacts/navigation-projection-probe/before"
output.mkdir(parents=True, exist_ok=True)
extracted = output / "MainViewModel.Navigation.cs"
extracted.write_text(
    "using DropSpace.App.Services;\nusing DropSpace.Core.Models;\n\n"
    "namespace DropSpace.App.ViewModels;\n\npublic sealed partial class MainViewModel\n{\n"
    + "\n".join(chunks) + "}\n", encoding="utf-8")
metadata = {
    "commit": commit,
    "original_path": source_path,
    "original_blob_sha256": hashlib.sha256(source_bytes).hexdigest(),
    "extracted_path": str(extracted.relative_to(ROOT)),
    "extracted_sha256": hashlib.sha256(extracted.read_bytes()).hexdigest(),
    "segments": segments,
    "modifications": "Only a partial-class wrapper and required using directives; method bodies are unchanged.",
}
(output / "extraction.json").write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
print(json.dumps(metadata, indent=2))
