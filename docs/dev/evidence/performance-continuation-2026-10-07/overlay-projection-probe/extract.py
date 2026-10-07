from pathlib import Path
import sys

source_root = Path(sys.argv[1])
output = Path(sys.argv[2]) if len(sys.argv) > 2 else Path(__file__).parent / "Generated.cs"
service = (source_root / "OverlayWindowService.cs").read_text() if (source_root / "OverlayWindowService.cs").exists() else (source_root / "Services/OverlayWindowService.cs").read_text()
window = (source_root / "OverlayWindow.xaml.cs").read_text()

def method(source, signature):
    start = source.index(signature)
    opening = source.index("{", start)
    depth = 0
    for end in range(opening, len(source)):
        if source[end] == "{":
            depth += 1
        elif source[end] == "}":
            depth -= 1
            if depth == 0:
                return source[start:end + 1]
    raise ValueError(f"Unclosed method: {signature}")

# Complete original method bodies, including dispatcher callbacks and authority
# reads. Dependencies are supplied by Fixture.cs; no scheduling body is rebuilt.
service_methods = "\n\n".join(method(service, signature) for signature in (
    "private void OnSnapshotChanged(",
    "private void OnExperienceChanged(",
    "private void ApplySnapshot(",
))
window_methods = "\n\n".join(method(window, signature) for signature in (
    "public void ApplySnapshot(",
    "private void OnMediaGeometryChanged(",
))
output.write_text("""// Generated verbatim from the selected source snapshot by extract.py.
using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.Logging;
namespace ProjectionProbe;
partial class ServiceFixture
{
""" + service_methods + "\n}\npartial class WindowFixture\n{\n" + window_methods + "\n}\n")
print(f"Extracted complete method bodies from {source_root}")
