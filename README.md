# DropSpace

DropSpace is a local-first Windows 11 workspace for temporarily holding file references and recent clipboard content, so it can be found, reused, and moved later without forcing immediate organization.

## Status

DropSpace is currently in **Phase 0: decisions and Windows feasibility validation**. The repository contains the product, UX, architecture, data, privacy, testing, and delivery specifications. Production application code has not been started yet.

The Phase 0 gates are:

- Event-driven clipboard monitoring for text and images.
- Real external file and folder drag-out.
- Reliable notification-area icon and hidden-window lifecycle.

The project does not advance to the production architecture until these Windows integration risks are validated.

## Product boundaries

- Windows 11 native desktop application.
- C#, .NET, WinUI 3, Windows App SDK, and MVVM.
- Local storage only by default.
- File records are references; removing a record never deletes or moves its source file.
- Clipboard source-app exclusions are best effort and are not treated as a privacy guarantee.
- AI, OCR, accounts, cloud sync, and browser extensions are outside the MVP and V1.1 scope.

## Documentation

- [Product specification](PRODUCT.md)
- [Feature catalogue](FEATURES.md)
- [UX specification](UX.md)
- [Design system](DESIGN_SYSTEM.md)
- [Architecture](ARCHITECTURE.md)
- [Data model](DATA_MODEL.md)
- [Windows integration](WINDOWS_INTEGRATION.md)
- [Privacy and threat model](PRIVACY.md)
- [Edge cases](EDGE_CASES.md)
- [Roadmap](ROADMAP.md)
- [Test plan](TEST_PLAN.md)
- [Decisions](DECISIONS.md)
- [Agent rules](AGENTS.md)

## Development workflow

Work is implemented on task branches. Each meaningful, verified change is committed and pushed; `main` remains buildable. A phase is merged only after its acceptance criteria pass and the related documentation is updated.

Build instructions will be added after the Phase 0 toolchain and packaged WinUI template are validated.

## License

No open-source license has been granted. All rights are reserved unless a license is added later.
