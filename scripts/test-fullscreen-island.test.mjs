import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

// These are source-level integration guards, not native HWND/game runtime tests.
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const read = relative => fs.readFileSync(path.join(root, relative), 'utf8');
const window = read('src/DropSpace.App/OverlayWindow.xaml.cs');
const service = read('src/DropSpace.App/Services/OverlayWindowService.cs');
const interop = read('src/DropSpace.App/Services/OverlayWindowInterop.cs');
const settings = read('src/DropSpace.App/Views/MainPage.Settings.cs');
const body = (source, start, end) => {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.ok(from >= 0 && to > from, `Missing source section: ${start}`);
  return source.slice(from, to);
};

test('fullscreen option has bilingual help and replaces the legacy control', () => {
  assert.match(settings, /IslandForceShowOverFullscreen/);
  assert.match(settings, /IslandForceShowOverFullscreenDescription/);
  assert.doesNotMatch(settings, /AddToggle\("ActivitiesFullscreen"/);
  assert.doesNotMatch(settings, /ForceShowOverFullscreen = v[\s\S]{0,120}SuppressOverFullscreen = !v/);
  for (const language of ['en-US', 'zh-CN']) {
    const resw = read(`src/DropSpace.App/Strings/${language}/Resources.resw`);
    for (const key of ['IslandForceShowOverFullscreen', 'IslandForceShowOverFullscreenDescription']) {
      assert.equal([...resw.matchAll(new RegExp(`name="${key}"`, 'g'))].length, 1);
    }
  }
});

test('polling observes both fullscreen directions, skips placement edits, and is released on shutdown', () => {
  const setup = body(service, 'private void UpdateFullscreenRefreshTimer()', 'private string? GetForegroundFullscreenMonitorId()');
  assert.match(setup, /if \(!_disposed\)/);
  assert.match(setup, /_fullscreenRefreshTimer\.Start\(\)/);
  assert.match(setup, /_fullscreenRefreshTimer\.Stop\(\)/);
  const tick = body(service, 'private void OnFullscreenRefreshTick(', 'private void OnViewModelPropertyChanged(');
  assert.match(tick, /_disposed \|\| _rebuildingSurfaces \|\| _placementEditingWindow is not null/);
  assert.match(tick, /CaptureForeground\(_surfaceMonitors\)/);
  assert.match(tick, /current != previous/);
  const dispose = body(service, 'public void Dispose()', 'private void OnSnapshotChanged(');
  assert.match(dispose, /_fullscreenRefreshTimer\.Stop\(\)/);
  assert.match(dispose, /_fullscreenRefreshTimer\.Tick -= OnFullscreenRefreshTick/);
});

test('Z-order maintenance keeps geometry and foreground input unchanged', () => {
  const maintain = body(interop, 'public static bool MaintainTopmostNoActivate(', 'public static bool Hide(');
  assert.match(maintain, /SetWindowPositionNoMove \| SetWindowPositionNoSize/);
  assert.match(maintain, /SetWindowPositionNoActivate \| SetWindowPositionNoOwnerZOrder/);
  assert.doesNotMatch(maintain, /SetForegroundWindow|SetFocus|AttachThreadInput|Activate\(\)/);
  const guard = body(window, 'internal void MaintainFullscreenVisibility()', 'private void HideImmediately()');
  assert.doesNotMatch(guard, /!_forceFullscreenPresentation|ForceShowOverFullscreen/);
  assert.match(guard, /!_isActiveWindow \|\| !_isVisible/);
  assert.match(guard, /!_nativeWindowSafeToShow/);
});

test('projected idle island is interactive and fullscreen click cannot activate the HWND', () => {
  const click = body(window, 'private async void OnCompactClicked(', 'private void OnCollapseClicked(');
  assert.match(click, /_experience\.Open\(DropSpace\.Core\.Island\.IslandPage\.Files\)/);
  assert.match(click, /if \(!presentation\.AllowActivation\) return;/);
  assert.ok(click.indexOf('if (!presentation.AllowActivation) return;') < click.indexOf('SetNoActivate(_windowHandle, false'));
  assert.ok(click.indexOf('if (!presentation.AllowActivation) return;') < click.indexOf('Activate();'));
  const pointer = body(window, 'private void OnSurfacePointerEntered(', 'private void OnSurfacePointerReleased(');
  assert.equal([...pointer.matchAll(/_presentedState == OverlayState\.Compact/g)].length, 2);
  assert.doesNotMatch(pointer, /_viewModel\.Snapshot\.State == OverlayState\.Compact/);
  assert.match(window, /EnsureVisualHostShown\(fullscreen\.AllowActivation\)/);
});

test('inactive monitor and unsafe native window protections precede forced display', () => {
  const apply = body(window, 'public void ApplySnapshot(', 'private bool _closing;');
  assert.ok(apply.indexOf('if (!_isActiveWindow)') < apply.indexOf('FullscreenOverlayPolicy.Resolve('));
  assert.ok(apply.indexOf('if (_suppressedForPlacementEdit)') < apply.indexOf('FullscreenOverlayPolicy.Resolve('));
  assert.ok(apply.indexOf('if (!PositionFixedHost())') < apply.indexOf('EnsureVisualHostShown(fullscreen.AllowActivation)'));
  assert.match(apply, /HideForNativeFailure\(\)/);
});
