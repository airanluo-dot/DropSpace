import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

// Source-level UI/localization contracts. Native rendering and interaction need Windows QA.
const read = path => readFileSync(new URL(`../${path}`, import.meta.url), 'utf8');
const page = read('src/DropSpace.App/Views/MainPage.xaml');
const code = read('src/DropSpace.App/Views/MainPage.xaml.cs');
const ai = read('src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs');
const resources = language => new Map([...read(`src/DropSpace.App/Strings/${language}/Resources.resw`)
  .matchAll(/<data name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>\s*<\/data>/g)]
  .map(match => [match[1], match[2]]));
const section = (source, start, end) => {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  assert.ok(from >= 0 && to > from, `Missing source section: ${start}`);
  return source.slice(from, to);
};

test('Island settings removes the two requested warning surfaces and obsolete settings action', () => {
  assert.doesNotMatch(page, /DropTrayCompatibility|DropTrayApiNotice|OpenDropTraySettingsButton|ExperimentalDragDetectionInfo/);
  assert.doesNotMatch(code, /OnOpenDropTraySettingsClicked/);
  for (const language of ['en-US', 'zh-CN']) {
    const strings = resources(language);
    assert.ok(![...strings.keys()].some(key => /^(DropTrayCompatibility|DropTrayApiNotice|OpenDropTraySettingsButton|ExperimentalDragDetectionInfo)/.test(key)));
  }
});

test('Smart detection keeps its persisted mode, other modes, event handler, and diagnostic action', () => {
  assert.match(page, /FileDragWakeModeSmart" Tag="SmartExperimental"/);
  assert.match(page, /FileDragWakeModeClassic" Tag="ClassicTopEdge"/);
  assert.match(page, /FileDragWakeModeDisabled" Tag="Disabled"/);
  assert.match(page, /SelectionChanged="OnFileDragWakeModeChanged"/);
  assert.match(page, /CopyDragCompatibilityReportButton"[^>]*Click="OnCopyDragCompatibilityReportClicked"/);
  assert.match(code, /_viewModel\.Settings with \{ FileDragWakeMode = mode \}/);
  assert.match(code, /_viewModel\.CopyDragCompatibilityReport\(\)/);
  assert.equal(resources('en-US').get('FileDragWakeModeSmart.Content'), 'Smart detection');
  assert.equal(resources('zh-CN').get('FileDragWakeModeSmart.Content'), '智能检测');
});

test('existing Music AI card and toggle clearly display Beta in both languages', () => {
  assert.match(ai, /Text = strings\.Get\("AiLyricsTitle"\)/);
  assert.match(ai, /_enabled.Header = strings\.Get\("AiLyricsEnabled"\)/);
  assert.match(ai, /AutomationProperties.SetName\(_enabled, strings\.Get\("AiLyricsEnabled"\)\)/);
  for (const language of ['en-US', 'zh-CN']) {
    const strings = resources(language);
    for (const key of ['AiLyricsTitle', 'AiLyricsEnabled', 'AiLyricsPlainBetaModel'])
      assert.match(strings.get(key), /Beta/, `${language} ${key}`);
    assert.match(strings.get('AiLyricsPlainBetaModel'), /Q8_0/);
    assert.match(strings.get('AiLyricsDescription'), language === 'en-US' ? /first full translation may take several minutes/ : /首次完成整首歌词翻译可能需要几分钟/);
    assert.match(strings.get('AiLyricsPlainBetaHelp'), language === 'en-US' ? /meaning errors/ : /含义错误/);
    assert.match(strings.get('AiLyricsTranslating'), language === 'en-US' ? /unfinished lines keep the original lyrics/ : /未完成的部分继续显示原歌词/);
    assert.doesNotMatch(strings.get('AiLyricsPlainBetaHelp'), /300|five minutes|5 分钟/);
  }
});

test('model selector uses selectable catalog only and maps Q8 Beta to its own localized help', () => {
  const selection = section(ai, 'foreach (var model in AiLyricsModelCatalog.All)', '_models.SelectionChanged');
  assert.doesNotMatch(selection, /Legacy|Standard|Compact/);
  assert.match(ai, /FindSelectable\(_editor.Settings.Lyrics.AiModelId\) \?\? AiLyricsModelCatalog.ExperimentalPlain/);
  assert.match(ai, /ExperimentalPlain.Id \? "AiLyricsPlainBetaModel"/);
  assert.match(ai, /ExperimentalPlain.Id \? "AiLyricsPlainBetaHelp"/);
});

test('optional 7B has its own localized label and resource cost in the existing consent dialog', () => {
  assert.match(ai, /ExperimentalLargePlain.Id \? "AiLyricsLargePlainBetaModel"/);
  assert.match(ai, /ExperimentalLargePlain.Id \? "AiLyricsLargePlainBetaHelp"/);
  for (const language of ['en-US', 'zh-CN']) {
    const strings = resources(language);
    assert.match(strings.get('AiLyricsLargePlainBetaModel'), /7B Q8_0.*Beta/);
    const help = strings.get('AiLyricsLargePlainBetaHelp');
    assert.match(help, /7\.98 GB/);
    assert.match(help, language === 'en-US' ? /more memory.*more video memory/ : /更多内存.*更多显存/);
    assert.match(help, language === 'en-US' ? /not guaranteed/ : /不保证/);
    assert.match(help, language === 'en-US' ? /remain until you remove them/ : /手动删除以释放空间/);
  }
  const install = section(ai, 'private async Task<bool> EnsureInstalledAsync(', 'private async Task<bool> SaveAsync(');
  assert.match(install, /ModelLabel\(model\), SizeLabel\(model\), model.Name, SourceLabel\(model\)/);
  assert.match(install, /ModelHelp\(model\)/);
  const consent = install.indexOf('ContentDialogLifetime.ShowAsync(dialog, token) != ContentDialogResult.Primary');
  const download = install.indexOf('_service.DownloadAsync(model.Id, consent: true');
  assert.ok(consent >= 0 && download > consent, 'Model download must follow explicit confirmation');
  assert.match(install, /DefaultButton = ContentDialogButton.Close/);
  const selection = section(ai, 'private void OnModelSelected(', 'private void OnDownload(');
  assert.match(selection, /AiTranslationEnabled && !await EnsureInstalledAsync\(model, generation, token\)/);
  assert.doesNotMatch(selection, /_service.DownloadAsync|AiTranslationEnabled\s*=/);
});

test('legacy files have cleanup-only controls, shown only for existing artifacts', () => {
  const legacy = section(ai, 'foreach (var model in AiLyricsModelCatalog.Legacy)', 'body.Children.Add(_legacyModels)');
  assert.match(legacy, /remove.Click \+= \(_, _\) => DeleteModel\(model\)/);
  assert.doesNotMatch(legacy, /_models.Items.Add|EnsureInstalledAsync|DownloadAsync|AiTranslationEnabled\s*=/);
  const inspection = section(ai, 'private async Task InspectAsync(', 'private void OnEnabled(');
  assert.match(inspection, /foreach \(var model in AiLyricsModelCatalog.Legacy\)\s*if \(_service.HasModelArtifacts\(model.Id\)\) removable.Add\(model.Id\)/);
  assert.match(ai, /remove.Visibility = _removable.Contains\(legacyId\) \? Visibility.Visible : Visibility.Collapsed/);
  assert.match(ai, /_legacyModels.Visibility = _legacyDeleteButtons.Keys.Any\(legacyId => _removable.Contains\(legacyId\)\) \? Visibility.Visible : Visibility.Collapsed/);
  const deletion = section(ai, 'private void DeleteModel(', 'private void OnClearCache(');
  assert.match(deletion, /ContentDialogLifetime.ShowAsync\(dialog, token\) != ContentDialogResult.Primary/);
  assert.match(deletion, /_service.DeleteModelAsync\(model.Id, token\)/);
  assert.doesNotMatch(deletion, /DownloadAsync|EnsureInstalledAsync/);
});

test('platform limitations remain documented outside the removed warning card', () => {
  const documentation = read('WINDOWS_INTEGRATION.md');
  assert.match(documentation, /No stable public API is documented for querying the Drop Tray toggle/);
  assert.match(documentation, /does not read internal feature flags, change this system setting/);
  assert.match(documentation, /without a persistent compatibility warning card/);
});

test('GPU preference is localized and separate from confirmed execution status', () => {
  assert.match(ai, /_gpuAcceleration.Header = strings.Get\("AiLyricsGpuAcceleration"\)/);
  assert.match(ai, /AutomationProperties.SetName\(_gpuAcceleration, strings.Get\("AiLyricsGpuAcceleration"\)\)/);
  assert.match(ai, /AutomationProperties.SetHelpText\(_gpuAcceleration, strings.Get\("AiLyricsGpuAccelerationHelp"\)\)/);
  assert.match(ai, /_gpuAcceleration.IsOn = settings.AiLyricsGpuAccelerationEnabled/);
  assert.match(ai, /_gpuAcceleration.IsEnabled = !_busy && !_inspecting/);
  for (const language of ['en-US', 'zh-CN']) {
    const strings = resources(language);
    assert.match(strings.get('AiLyricsGpuAcceleration'), /GPU/);
    const help = strings.get('AiLyricsGpuAccelerationHelp');
    assert.match(help, language === 'en-US' ? /unavailable or fails.*CPU/ : /GPU 不可用或运行失败时自动改用 CPU/);
    assert.match(help, language === 'en-US' ? /Turn this off to use only the CPU/ : /关闭后仅使用 CPU/);
    assert.doesNotMatch(help, /CUDA|RTX|Radeon|GPU active|正在使用 GPU/);
  }
});

test('GPU toggle only saves preference through the existing guarded operation path', () => {
  const toggle = section(ai, 'private void OnGpuAcceleration(', 'private void OnModelSelected(');
  assert.match(toggle, /if \(_syncing\) return/);
  assert.match(toggle, /var preferGpu = _gpuAcceleration.IsOn/);
  assert.match(toggle, /StartOperation\(async \(generation, _\)/);
  assert.match(toggle, /await SaveAsync\(generation/);
  assert.match(toggle, /Lyrics = settings.Lyrics with \{ AiLyricsGpuAccelerationEnabled = preferGpu \}/);
  assert.doesNotMatch(toggle, /AiTranslationEnabled\s*=|EnsureInstalledAsync|DownloadAsync|ContentDialog|Process\.Start/);
});

test('progressive copy distinguishes partial lines from a completed full translation', () => {
  for (const language of ['en-US', 'zh-CN']) {
    const strings = resources(language);
    const status = strings.get('AiLyricsTranslating');
    assert.match(status, language === 'en-US' ? /Completed translations appear line by line/ : /已完成的译文会逐行显示/);
    assert.match(status, language === 'en-US' ? /unfinished lines keep the original lyrics/ : /未完成的部分继续显示原歌词/);
    assert.notEqual(status, strings.get('AiLyricsTranslationComplete'));
  }
});
