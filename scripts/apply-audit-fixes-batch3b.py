from pathlib import Path

path = Path("src/DropSpace.App/ViewModels/MainViewModel.cs")
raw = path.read_bytes()
newline = "\r\n" if raw.count(b"\r\n") > raw.count(b"\n") // 2 else "\n"
text = raw.decode("utf-8").replace("\r\n", "\n")
old = "if (!await _projectionLoadGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))"
new = "if (!await _projectionLoadGate.WaitAsync(0, cancellationToken))"
if text.count(old) != 1:
    raise RuntimeError(f"expected exactly one UI continuation match, found {text.count(old)}")
text = text.replace(old, new, 1)
if newline == "\r\n":
    text = text.replace("\n", "\r\n")
path.write_bytes(text.encode("utf-8"))
print("UI projection continuation corrected")
