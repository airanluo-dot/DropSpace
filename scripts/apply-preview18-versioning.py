from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> tuple[str, str]:
    raw = (ROOT / path).read_bytes()
    newline = "\r\n" if raw.count(b"\r\n") >= max(1, raw.count(b"\n") // 2) else "\n"
    return raw.decode("utf-8").replace("\r\n", "\n"), newline


def write(path: str, text: str, newline: str) -> None:
    data = text if newline == "\n" else text.replace("\n", "\r\n")
    (ROOT / path).write_bytes(data.encode("utf-8"))


def replace_once(path: str, old: str, new: str) -> None:
    text, newline = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}")
    write(path, text.replace(old, new, 1), newline)


replace_once("RELEASE_VERSION", "v0.3.0-preview.17\n", "v0.3.0-preview.18\n")

replace_once(
    "README.md",
    "DropSpace **v0.2.1 is the current Stable release and v0.3.0-preview.17 is the current Preview**. The repository contains the WinUI 3 application, a standard per-user installer, portable and MSIX deployment paths, automated lifecycle tests, Windows CI/release automation, and the product/engineering specifications that define its safety boundaries.\n\nLatest Stable: [v0.2.1](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.1). Latest Preview: [v0.3.0-preview.17](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0-preview.17). The optional Preview update channel receives both Stable and Preview releases and always selects the highest eligible SemVer without downgrading.\n\nThe **v0.3.0-preview.17** architecture audit adds recoverable projections, shared database write ownership, atomic settings mutation, awaited shutdown, bounded automatic clipboard propagation, confined staged-file imports and generation-invalidated preview caching. See the [release notes](.github/release-notes/v0.3.0-preview.17.md) and [15-part architecture audit](docs/audit/2026-09-07/architecture-audit-zh.md). Publication requires the final Windows build/release and website/API gates; real Windows OS/DPI/OLE/accessibility, two-device and deployed Worker/browser evidence remains conditional.\n",
    "DropSpace **v0.2.1 is the current Stable release and v0.3.0-preview.18 is the current Preview**. The repository contains the WinUI 3 application, a standard per-user installer, portable and MSIX deployment paths, automated lifecycle tests, Windows CI/release automation, and the product/engineering specifications that define its safety boundaries.\n\nLatest Stable: [v0.2.1](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.2.1). Latest Preview: [v0.3.0-preview.18](https://github.com/airanluo-dot/DropSpace/releases/tag/v0.3.0-preview.18). The optional Preview update channel receives both Stable and Preview releases and always selects the highest eligible SemVer without downgrading.\n\nThe **v0.3.0-preview.18** architecture-hardening release closes the Preview.17 audit with transactional retention/settings ownership, reparse-safe file boundaries, durable payload cleanup, resumable verified updates, bounded Worker admission, and scalable keyset/FTS search. `MainViewModel` delegates projection and settings transactions to dedicated coordinators. See the [Preview.18 release notes](.github/release-notes/v0.3.0-preview.18.md) and [architecture audit](docs/audit/2026-09-07/architecture-audit-zh.md). Hosted publication gates remain separate from real Windows OS/DPI/OLE/accessibility, two-device, and deployed Worker/browser evidence.\n\nThe **v0.3.0-preview.17** architecture audit adds recoverable projections, shared database write ownership, atomic settings mutation, awaited shutdown, bounded automatic clipboard propagation, confined staged-file imports and generation-invalidated preview caching. See the [release notes](.github/release-notes/v0.3.0-preview.17.md) and [15-part architecture audit](docs/audit/2026-09-07/architecture-audit-zh.md). Publication requires the final Windows build/release and website/API gates; real Windows OS/DPI/OLE/accessibility, two-device and deployed Worker/browser evidence remains conditional.\n",
)

replace_once(
    "ROADMAP.md",
    "The v0.2.1 Stable production slice remains the Stable baseline. v0.3.0-preview.16 is the current full-hardening Preview: it converges native/OLE ownership, service initialization and rollback, image decode budgets, native callback isolation, local-network authority, DropLink/DNS-SD lifecycle, revoke-store retention, updater cancellation, and x64 release validation. Hosted Windows checks are the build and packaging gate; real OS/DPI/OLE/accessibility, two-device transfer, and Worker/browser evidence remain explicit operational gates. Commercial signing remains optional and credential-gated.\n\nPhase 0 boundary adapters are implemented rather than left as throwaway spikes. Automated Windows lifecycle, drag, projection, DPI, update, and packaging coverage remains paired with real-target desktop evidence for Explorer/Desktop drag-in, Overlay drag-out, mixed-DPI geometry, fullscreen behavior, animation feel, and tray recreation after Explorer restart.\n\n## v0.3.0-preview.16 delivery slice\n",
    "The v0.2.1 Stable production slice remains the Stable baseline. v0.3.0-preview.18 is the current architecture-hardening Preview: it closes the Preview.17 audit across transactional retention/settings ownership, reparse-safe file boundaries, payload cleanup recovery, updater trust/resume behavior, bounded Worker admission, scalable FTS/keyset projection, and release governance. Hosted Windows checks are the build and packaging gate; real OS/DPI/OLE/accessibility, two-device transfer, and Worker/browser evidence remain explicit operational gates. Preview signing remains optional; Stable publication is signing-gated.\n\nPhase 0 boundary adapters are implemented rather than left as throwaway spikes. Automated Windows lifecycle, drag, projection, DPI, update, and packaging coverage remains paired with real-target desktop evidence for Explorer/Desktop drag-in, Overlay drag-out, mixed-DPI geometry, fullscreen behavior, animation feel, and tray recreation after Explorer restart.\n\n## v0.3.0-preview.18 delivery slice\n\n- Close the full architecture audit without expanding the product boundary: serialize retention selection/commit, report actual affected rows, and durably recover app-owned payload cleanup failures.\n- Enforce reparse-aware DropLink and ZIP file boundaries, preserve partial-finalization evidence, redact crash markers, and bound network/logger failure paths.\n- Move settings runtime transactions and collection projection orchestration out of `MainViewModel`; use 200-item keyset continuation and schema-v4 FTS5 trigram search instead of a fixed visible projection/deep SQL OFFSET.\n- Add resumable HTTP Range update downloads with final full SHA-256 verification, whole-chain Authenticode revocation checking, bounded Worker streaming/admission, immutable Actions SHAs, explicit publication, and Stable signing gates.\n- Re-run Core/Infrastructure/App, WinUI, portable/MSIX/identity/Inno, installer update/upgrade/uninstall, en-US/zh-CN smoke, Worker, and website/browser-route gates before publishing Preview.18.\n\n## v0.3.0-preview.16 delivery slice\n",
)
