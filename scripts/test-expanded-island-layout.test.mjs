import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const read = path => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
const island = read('src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml');
const shared = read('src/DropSpace.App/Views/Island/MediaExpandedView.xaml');

// These are source-level ownership/layout contracts, not native pixel or DPI tests.
test('only the expanded island opts into the new music layout', () => {
  const overlay = read('src/DropSpace.App/OverlayWindow.xaml');
  const musicPage = read('src/DropSpace.App/Views/Music/MusicPage.cs');
  assert.match(overlay, /<island:ExpandedIslandMusicView x:Name="MusicExpanded" Margin="18,0" Visibility="Collapsed"\/>/);
  assert.match(musicPage, /_nowPlaying = new MediaExpandedView \{ ViewModel = media, MinHeight = 280, Height = 380 \}/);
  assert.doesNotMatch(musicPage, /new ExpandedIslandMusicView/);
  assert.match(shared, /<Grid Padding="28,24" RowSpacing="12">/);
  assert.doesNotMatch(shared, /NextLyric/);
  assert.match(shared, /<ScrollViewer x:Name="CurrentLyricsViewport"[^>]*VerticalScrollMode="Auto"/);
});

test('the island reserves the bottom for transport without an empty status footer', () => {
  assert.match(island, /<Grid Padding="28,24,28,16" RowSpacing="12">/);
  assert.match(island, /<RowDefinition Height="Auto"\/><RowDefinition Height="\*"\/><RowDefinition Height="Auto"\/>/);
  assert.match(island, /x:Name="PlaybackStatus"[^>]*Visibility="Collapsed"/);
  assert.match(island, /x:Name="CurrentLyricsViewport"[^>]*VerticalScrollMode="Auto"/);
});

test('the preview remains one line and is distinguished by more than color', () => {
  const preview = island.match(/<TextBlock x:Name="NextLyric"[^>]*\/>/)?.[0];
  assert.ok(preview);
  assert.match(preview, /FontWeight="Normal"/);
  assert.match(preview, /FontSize="13"/);
  assert.match(preview, /TextWrapping="NoWrap"/);
  assert.match(preview, /MaxLines="1"/);
  assert.match(preview, /TextTrimming="CharacterEllipsis"/);
  assert.match(preview, /Foreground="\{ThemeResource TextFillColorSecondaryBrush\}"/);
  assert.doesNotMatch(preview, /Opacity=/);
  assert.match(island, /x:Name="NextLyricPreview"[^>]*BorderThickness="0,1,0,0"/);
  assert.match(island, /Glyph="&#xE70D;"/);
  assert.doesNotMatch(island, /Text="下一句[：:]/);
});

test('preview accessibility is localized and playback keeps the shared seek policy', () => {
  const code = read('src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml.cs');
  assert.match(code, /AutomationProperties.SetName\(NextLyric,.*NextLyricLabel/);
  assert.match(code, /readonly MediaSeekInteraction _seekInteraction/);
  assert.doesNotMatch(code, /class MediaSeekInteraction/);
  for (const culture of ['en-US', 'zh-CN']) {
    const resources = read(`src/DropSpace.App/Strings/${culture}/Resources.resw`);
    assert.equal((resources.match(/name="MediaNextLyricLabel"/g) ?? []).length, 1);
  }
});
