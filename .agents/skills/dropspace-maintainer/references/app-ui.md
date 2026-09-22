# App and UI guide

Use this reference when changing the Windows application, its user interface, or an internal
service contract. It is a project map, not a checklist.

## Product surfaces

- **Main window:** Space, Clipboard, Pinned, Search, Music, and Settings live under
  `src/DropSpace.App/Views` and are coordinated by view models in
  `src/DropSpace.App/ViewModels`.
- **Dynamic Island:** compact and expanded quick-access surfaces live under
  `src/DropSpace.App/Views/Island`; overlay lifecycle, placement, material, and motion services
  live under `src/DropSpace.App/Services`.
- **Core behavior:** reusable models, policies, state machines, and service contracts live in
  `src/DropSpace.Core`.
- **Infrastructure:** SQLite, payload storage, update transports, previews, lyrics, networking,
  and other concrete adapters live in `src/DropSpace.Infrastructure`.
- **Composition root:** `src/DropSpace.App/App.xaml.cs` registers the implementations used by
  the running app.

## Visual language

DropSpace should feel like a compact Windows utility rather than a web dashboard. The established
language uses WinUI controls, restrained surfaces, clear hierarchy, system typography, shared
theme resources, and the existing accent palette. Space and Clipboard remain visually easy to
distinguish, while Pinned and Search reuse the same item language.

The detailed source of truth is `DESIGN_SYSTEM.md`. Useful sections include:

- typography, color, spacing, corner radius, materials, and icons;
- shell, item-row, card, button, and context-menu patterns;
- interaction states, motion, light/dark themes, high contrast, scaling, and content language.

`UX.md` explains navigation, user flows, keyboard behavior, drag interactions, settings, window
behavior, and the Dynamic Island experience. Read the section for the surface being changed rather
than treating the entire history as current task instructions.

## How the application interfaces fit together

The usual flow is:

```text
View -> ViewModel -> use case or Core interface -> App/Infrastructure implementation
```

Core interface entry points are easy to discover in:

- `src/DropSpace.Core/Abstractions` for repositories, settings, storage, localization, startup,
  and updates;
- `src/DropSpace.Core/Media` for media-session and NetEase enhancement contracts;
- `src/DropSpace.Core/Lyrics` for lyric providers and documents;
- `src/DropSpace.Core/Actions` for item actions and image transforms;
- `src/DropSpace.Core/Preview` for preview providers and caches.

Concrete implementations are normally registered in `App.xaml.cs`. When tracing an interface,
search for its registration there, then follow the implementation into `DropSpace.App/Services`
or `DropSpace.Infrastructure`.

## Adding or extending a feature

A typical feature may involve a Core model or interface, an implementation in App or
Infrastructure, registration in the composition root, a ViewModel-facing operation, and a XAML
surface. Small UI-only changes may need only the existing View and ViewModel. Follow the nearest
working feature instead of creating a new abstraction for every change.

Localized XAML and imperative strings are under:

```text
src/DropSpace.App/Strings/en-US/Resources.resw
src/DropSpace.App/Strings/zh-CN/Resources.resw
```

Shared visual values should come from the existing resource dictionaries and design tokens. The
current code is the best example of how a specific control, service, or registration is wired.
