#!/usr/bin/env python3
"""Build an ad-hoc signed development/release candidate. Does not publish or assert parity."""
import argparse
import hashlib
import json
import os
import pathlib
import plistlib
import shutil
import stat
import subprocess
import tempfile
import time
import zipfile

ROOT = pathlib.Path(__file__).resolve().parents[1]
REPO = ROOT.parent

def run(*args, **kwargs):
    return subprocess.run([str(x) for x in args], check=True, **kwargs)

def digest(file):
    with open(file, "rb") as stream:
        hasher = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(chunk)
        return hasher.hexdigest()

def sign(file):
    run("/usr/bin/codesign", "--force", "--sign", "-", "--timestamp=none", file)
    run("/usr/bin/codesign", "--verify", "--strict", file)

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--skip-build", action="store_true", help="Package previously built Release products; byte validation still runs")
    parser.add_argument("--output", type=pathlib.Path, default=ROOT / "dist" / "development")
    args = parser.parse_args()
    output = args.output.resolve()
    if output.exists():
        raise SystemExit("Output already exists; choose a new directory to preserve earlier evidence.")
    version = (ROOT / "RELEASE_VERSION").read_text().strip()
    tag = "macos-" + version
    if not args.skip_build:
        run(ROOT / "scripts/build.sh", env={**os.environ, "CONFIGURATION": "Release"})
        run("swift", "build", "--package-path", ROOT, "-c", "release", "--product", "DropSpaceUpdateHelper")
    helper_bin = subprocess.check_output(["swift", "build", "--package-path", str(ROOT), "-c", "release", "--show-bin-path"], text=True).strip()
    sources = {
        "DropSpaceUpdateHelper": pathlib.Path(helper_bin) / "DropSpaceUpdateHelper",
        "plain-lyrics-worker": ROOT / "build/workers/bin/plain-lyrics-worker",
        "language-worker": ROOT / "build/language-worker/bin/language-worker",
    }
    app_source = ROOT / "build/Build/Products/Release/DropSpace.app"
    if not app_source.is_dir() or any(not file.is_file() for file in sources.values()):
        raise SystemExit("A Release app or required native helper is missing. Build all products first.")
    output.mkdir(parents=True)
    with tempfile.TemporaryDirectory(prefix="package-", dir=ROOT / "build") as temporary:
        stage = pathlib.Path(temporary)
        app = stage / "DropSpace.app"
        shutil.copytree(app_source, app, symlinks=True)
        helpers = app / "Contents/Helpers"
        helpers.mkdir(exist_ok=True)
        for name, source in sources.items():
            target = helpers / name
            shutil.copy2(source, target)
            target.chmod(0o755)
            sign(target)
        with (app / "Contents/Info.plist").open("rb") as stream:
            info = plistlib.load(stream)
        if info.get("DropSpaceReleaseTag") != tag or info.get("CFBundleIdentifier") != "io.github.airanluo-dot.DropSpace.macOS":
            raise SystemExit("Version or bundle identity disagrees with the release source.")
        licenses = app / "Contents/Resources/ThirdPartyLicenses"
        licenses.mkdir(parents=True, exist_ok=True)
        for directory, name in [("llama.cpp", "llama.cpp-LICENSE.txt"), ("fastText", "fastText-LICENSE.txt")]:
            shutil.copy2(ROOT / "build/worker-sources" / directory / "LICENSE", licenses / name)
        run("python3", ROOT / "scripts/embed-language-runtime.py", "--app", app)
        shutil.copy2(ROOT / "docs/DISTRIBUTION.md", app / "Contents/Resources/Distribution.md")
        for file in [app / "Contents/MacOS/DropSpace", *helpers.iterdir()]:
            architecture = subprocess.check_output(["/usr/bin/lipo", "-archs", str(file)], text=True).strip()
            if architecture != "arm64": raise SystemExit(f"Unexpected architecture: {file.name}: {architecture}")
            load_commands = subprocess.check_output(["xcrun", "vtool", "-show-build", str(file)], text=True)
            if "minos 26.0" not in load_commands: raise SystemExit(f"Unexpected minimum OS: {file.name}")
        sign(app)
        run("/usr/bin/codesign", "--verify", "--deep", "--strict", app)
        for name in ["plain-lyrics-worker", "language-worker"]:
            run(helpers / name, "--version")
        # Stable order/time/modes make the ZIP reproducible for the same signed input bytes.
        epoch = int(os.environ.get("SOURCE_DATE_EPOCH", subprocess.check_output(["git", "-C", str(REPO), "show", "-s", "--format=%ct", "HEAD"], text=True).strip()))
        stamp = time.gmtime(max(epoch, 315532800))[:6]
        archive = output / "DropSpace-macOS-arm64.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as stream:
            for file in [app, *sorted(app.rglob("*"))]:
                if file.is_symlink(): raise SystemExit(f"Release archive forbids symlinks: {file}")
                relative = file.relative_to(stage).as_posix() + ("/" if file.is_dir() else "")
                entry = zipfile.ZipInfo(relative, stamp)
                entry.create_system = 3
                entry.external_attr = ((stat.S_IFDIR | 0o755) if file.is_dir() else (stat.S_IFREG | (0o755 if os.access(file, os.X_OK) else 0o644))) << 16
                entry.compress_type = zipfile.ZIP_DEFLATED
                stream.writestr(entry, b"" if file.is_dir() else file.read_bytes(), compresslevel=9)
        # DMG has filesystem metadata; reproducibility here means a repeatable recipe,
        # not a claim that hdiutil chooses identical filesystem UUIDs between runs.
        os.symlink("/Applications", stage / "Applications")
        (stage / "READ ME.txt").write_text(
            "DropSpace for macOS — " + version + "\n\n"
            "Not notarized by Apple. No Developer ID signature. Locally ad-hoc signed.\n"
            "Requires macOS 26 or later and Apple Silicon.\n"
            "Copy DropSpace to Applications, eject this disk, then open the copied app.\n"
            "If blocked and you trust this download, use System Settings > Privacy & Security > Open Anyway.\n"
            "Do not disable Gatekeeper, SIP, or global security protections.\n"
            "https://support.apple.com/en-us/102445\n", encoding="utf-8")
        dmg = output / "DropSpace-macOS-arm64.dmg"
        run("/usr/bin/hdiutil", "create", "-quiet", "-volname", "DropSpace " + version, "-srcfolder", stage, "-format", "UDZO", "-fs", "APFS", dmg)
        run("/usr/bin/hdiutil", "verify", dmg)
        def asset(file):
            return {"name": file.name, "url": f"https://github.com/airanluo-dot/DropSpace/releases/download/{tag}/{file.name}", "size": file.stat().st_size, "sha256": digest(file)}
        notes_file = ROOT / "docs/RELEASE-NOTES.md"
        notes = notes_file.read_text() if notes_file.exists() else "Development candidate. Feature parity and installation acceptance remain incomplete. Not a public release."
        manifest = {"schemaVersion": 1, "platform": "macos", "architecture": "arm64", "tag": tag,
            "channel": "beta" if "-beta." in tag else "stable", "bundleID": info["CFBundleIdentifier"],
            "bundleVersion": info["CFBundleVersion"], "minimumOS": info["LSMinimumSystemVersion"],
            "windowsBaseline": "v0.3.2-beta.7", "signing": "ad-hoc", "notarized": False,
            "archive": asset(archive), "installer": asset(dmg), "releaseNotes": notes,
            "releaseURL": f"https://github.com/airanluo-dot/DropSpace/releases/tag/{tag}"}
        manifest_file = output / "update-manifest-macos.json"
        manifest_file.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n")
        (output / "SHA256SUMS-macos.txt").write_text("".join(f"{digest(file)}  {file.name}\n" for file in [archive, dmg, manifest_file]))
        receipt = {"sourceCommit": subprocess.check_output(["git", "-C", str(REPO), "rev-parse", "HEAD"], text=True).strip(),
            "workingTreeDirty": bool(subprocess.check_output(["git", "-C", str(REPO), "status", "--porcelain"], text=True).strip()),
            "xcode": subprocess.check_output(["xcodebuild", "-version"], text=True).strip(),
            "platform": "macos-arm64", "signing": "ad-hoc", "notarized": False, "parityAccepted": False,
            "firstDownloadedInstallValidated": False, "files": {str(file.relative_to(stage)): digest(file) for file in sorted(app.rglob("*")) if file.is_file()}}
        (output / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
    print(f"Candidate package prepared at {output}. Publication and functional acceptance have not been performed.")

if __name__ == "__main__": main()
