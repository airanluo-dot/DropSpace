# DropSpace Design System

Status: Fluent design specification v1. No visual prototype was generated in this phase.

## Direction

Use Windows 11 Fluent design as a native utility, drawing structural cues from Windows Settings and PowerToys: calm Mica foundation, restrained cards, compact rows, clear status, and native menus. Avoid dashboard metrics, oversized marketing headings, floating glass panels, and excessive gradients.

## Design principles

- Content first; material stays behind content.
- Density supports scanning without becoming cramped.
- State is communicated by text/icon/shape, never color alone.
- Native control behavior beats custom visual novelty.
- Privacy and errors remain visible but non-alarming.

## Typography

Use system Segoe UI Variable through WinUI theme resources.

| Role | Size | Weight | Use |
|---|---:|---:|---|
| Display | 28 | 600 | Empty-state/title moments only |
| Page title | 24 | 600 | Top-level page |
| Section title | 18 | 600 | Groups and settings sections |
| Body strong | 14 | 600 | Item title |
| Body | 14 | 400 | Standard content |
| Caption | 12 | 400 | Metadata/status |
| Code | 13 | 400 | Code/path preview, Cascadia Mono fallback |

Respect Windows text scaling; do not clamp user accessibility sizes.

## Color

Use theme resources and system accent rather than fixed palette values.

- Window foundation: Mica with system fallback.
- Content layer: `LayerFillColorDefaultBrush`/equivalent theme resource.
- Cards: `CardBackgroundFillColorDefaultBrush` and standard stroke.
- Accent: system accent for focus, selection edge, and primary actions.
- Error, warning, success: system semantic brushes with accompanying icon/text.
- Color swatches show a checker/outline when very light or transparent.

Custom hard-coded colors are limited to content previews such as a captured color value.

## Spacing

Base grid: 4 effective pixels.

- 4: icon/text micro-gap.
- 8: control internal gap.
- 12: compact row padding.
- 16: card/group spacing.
- 24: page gutters on compact widths.
- 32: page gutters on wide widths and major section separation.

## Corner radius

Use platform tokens where available.

- Small controls/chips: 4 px.
- Cards, search box, dialogs: 8 px.
- Quick Panel window content: 12 px only if the window chrome supports it naturally.
- Do not nest several rounded containers without a hierarchy reason.

## Materials

### Mica

Default primary-window backdrop. Page roots remain transparent enough for the material to read. Opaque content surfaces provide legibility.

### Acrylic

Only for transient surfaces such as flyouts where the system applies it appropriately. Do not use desktop acrylic as the main window by default.

### Fallback

High contrast, battery saver, remote sessions, unsupported hardware, or OS policy may remove transparency. Every surface must remain complete with solid theme brushes.

## Icons

- Use Fluent/System icons through `SymbolIcon`, `FontIcon`, or official asset sources.
- 16 px in rows/menus, 20 px in navigation, 24 px for empty-state support.
- Do not use emoji as product icons.
- Add accessible names; avoid icons when text is clearer.

## Layout components

### App shell

`NavigationView` left pane, integrated title bar, global search, transparent page root, bounded content width only for Settings.

### Item row

48–64 px minimum height depending on preview. Structure: type icon/thumbnail, title and metadata, state badge, primary action, overflow. Multi-line text caps at two lines in collection views.

### Card

Use for settings groups, empty states, and image previews—not every list row. Border and background come from theme resources.

### Buttons

- Primary: one per focused surface.
- Standard: normal actions.
- Subtle: row actions and toolbar icons.
- Destructive: confirmation surface only; removal from DropSpace is not styled like deleting a disk file.

### Context menu

Native `MenuFlyout`, standard ordering, separators by action group, keyboard access, Acrylic/system backdrop.

## Interaction states

- Hover: theme hover fill; never reveal the only way to perform an action.
- Pressed: theme pressed fill and subtle scale only if platform control provides it.
- Selected: accent edge/fill plus selection semantics.
- Focused: visible system focus rectangle, not replaced by selection.
- Disabled: platform disabled resources and explanatory tooltip when useful.
- Missing: warning icon, neutral tinted surface, explanatory text.
- Dragging: source opacity change plus standard drag visual.
- Drag over: 2 px accent border, soft accent fill, explicit operation text.
- Paused: persistent status chip and text; not red unless recording failed.

## Motion

- Prefer system connected/entrance animations and theme transitions.
- 100–200 ms for local state changes; no decorative loops.
- Quick Panel should appear promptly with restrained opacity/scale using platform composition.
- Honor system animation and reduced-motion settings; correctness never depends on animation completion.

## Light and dark modes

- All colors come from theme resources except user content.
- Thumbnails have a neutral boundary in both themes.
- Validate contrast of accent selection with custom system accent colors.
- Images and color swatches are not automatically tinted.

## High contrast and accessibility

- Support high-contrast themes without Mica/Acrylic dependency.
- Minimum text contrast follows WCAG AA where platform resources do not already ensure it.
- Controls expose name, role, state, value, and help text via UI Automation.
- Reading order equals visual order.
- Status changes important to the current task use live-region announcements without announcing every clipboard event.
- Never expose captured clipboard text through an accessibility announcement unless the user focused it.

## Responsive and scaling rules

- Under 840 px width, collapse NavigationView and details pane.
- Under 720 px, toolbar actions move into overflow; minimum window prevents unusable layouts.
- Use effective pixels, XAML layout, and rasterization-scale-aware thumbnails.
- Test 100%, 125%, 150%, 200%, mixed-DPI display moves, RTL readiness, and 200% text scaling.

## Content language

- Short, direct, non-technical.
- Say “Remove from DropSpace,” never “Delete file.”
- Say “Clipboard recording is paused,” not “Listener disabled.”
- Explain best-effort limits near exclusions and sensitive-data controls.
- Do not show full sensitive payloads in transient messages.

## Visual review checklist

- Does the UI still work with transparency off and high contrast on?
- Are Space and Clipboard distinguishable without relying on page location?
- Are hover-only actions reachable by keyboard/touch?
- Are long file paths and text constrained without hiding critical status?
- Is one primary action visually clear per surface?
- Do density, corners, icons, and typography use Windows tokens consistently?
