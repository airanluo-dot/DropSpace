// Production integrity gate; this scans resource records, not functional test cases.
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { checkModuleResources } from '../modules/check-module-resources.mjs';

const defaultRoot = fileURLToPath(new URL('../', import.meta.url));
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const read = file => fs.readFileSync(file, 'utf8');
const relative = (root, file) => path.relative(root, file).replaceAll('\\', '/');
const sorted = values => [...values].sort();
const same = (a, b) => JSON.stringify(sorted(a)) === JSON.stringify(sorted(b));
function files(root, extensions) {
  if (!fs.existsSync(root)) return [];
  return fs.readdirSync(root, { withFileTypes: true }).flatMap(entry => {
    if (['bin', 'obj', 'node_modules', 'dist', 'dist-static', 'test-results'].includes(entry.name)) return [];
    const name = path.join(root, entry.name);
    return entry.isDirectory() ? files(name, extensions) : extensions.includes(path.extname(name)) ? [name] : [];
  });
}

// JSON.parse accepts duplicate properties. Parse first, then inspect every object key token.
function json(file) {
  const text = read(file);
  const result = JSON.parse(text);
  const stack = []; let cursor = 0;
  while (cursor < text.length) {
    const char = text[cursor++];
    if (char === '{') stack.push(new Set());
    else if (char === '[') stack.push(null);
    else if (char === '}' || char === ']') stack.pop();
    else if (char === '"') {
      const start = cursor - 1;
      while (cursor < text.length) {
        if (text[cursor] === '\\') cursor += 2;
        else if (text[cursor++] === '"') break;
      }
      const token = JSON.parse(text.slice(start, cursor));
      if (/^\s*:/.test(text.slice(cursor)) && stack.at(-1)) {
        if (stack.at(-1).has(token)) throw Error(`${file}: duplicate JSON resource/property '${token}'`);
        stack.at(-1).add(token);
      }
    }
  }
  return result;
}
function getDom(root) {
  const require = createRequire(path.join(root, 'website/_source/package.json'));
  try { return require('jsdom').JSDOM; }
  catch { throw Error('Install the existing parser with npm ci --prefix website/_source --ignore-scripts before checking XML/HTML resources.'); }
}
function resw(file, JSDOM) {
  const document = new JSDOM(read(file), { contentType: 'application/xml' }).window.document;
  if (document.documentElement.localName !== 'root') throw Error(`${file}: .resw root must be <root>`);
  const values = {};
  for (const item of document.documentElement.children) {
    if (item.localName !== 'data') continue;
    const id = item.getAttribute('name');
    if (!id || Object.hasOwn(values, id)) throw Error(`${file}: missing/duplicate resource '${id}'`);
    const value = [...item.children].filter(node => node.localName === 'value');
    if (value.length !== 1) throw Error(`${file}: '${id}' requires exactly one <value>`);
    values[id] = value[0].textContent;
  }
  if (!Object.keys(values).length) throw Error(`${file}: no resources`);
  return values;
}
// Match the pinned Inno Setup 7.0.2 compiler and FmtMessage in two stages.
// Messages expands every %n, even in %%n; CustomMessages skips percent pairs
// during newline expansion. FmtMessage then handles %% and single-digit %1..%9.
// See docs/dev/installer-localization-gate.md for the versioned source references.
export function installerMessageTokens(value, section) {
  if (!['Messages', 'CustomMessages'].includes(section)) throw Error(`Unknown installer message section ${section}`);
  let compiled = '';
  if (section === 'Messages') compiled = value.replaceAll('%n', '\r\n');
  else {
    for (let index = 0; index < value.length; index++) {
      if (value[index] === '%' && index + 1 < value.length) {
        compiled += value[index + 1] === 'n' ? '\r\n' : value.slice(index, index + 2);
        index++;
      } else compiled += value[index];
    }
  }
  const tokens = [];
  for (let index = 0; index < compiled.length; index++) {
    if (compiled[index] === '\r' && compiled[index + 1] === '\n') {
      tokens.push('newline'); index++;
    } else if (compiled[index] === '%') {
      const next = compiled[index + 1];
      if (next === '%') { tokens.push('percent:escaped'); index++; }
      else if (next && /[1-9]/.test(next)) { tokens.push(`argument:${next}`); index++; }
      else tokens.push('percent:literal');
    }
  }
  return sorted(tokens);
}

// All repository-owned overrides belong in the reviewed locale .isl files.
// Reject qualified entries too: main-script entries take final precedence and
// must not create a second resource definition/review path. Empty values also
// override the wizard text. Conditional/preprocessor content fails closed here.
export function checkInstallerScriptOverrides(text, filename = 'installer/DropSpace.iss') {
  let section = '';
  for (const [index, line] of text.replace(/^\uFEFF/, '').split(/\r?\n/).entries()) {
    const trimmed = line.trim();
    const heading = trimmed.match(/^\[([^\]]+)\]$/);
    if (heading) { section = heading[1].toLowerCase(); continue; }
    if (!['messages', 'custommessages'].includes(section) || !trimmed || trimmed.startsWith(';')) continue;
    throw Error(`${filename}:${index + 1}: main-script [${section === 'messages' ? 'Messages' : 'CustomMessages'}] overrides are forbidden; move this entry to the reviewed installer/localization/<locale>.isl files`);
  }
}

function placeholders(value, id = '') {
  const escaped = value.replaceAll('{{', '').replaceAll('}}', '');
  // C# composite formats and JS named interpolation must preserve identifiers/format specifiers.
  const markers = [...escaped.matchAll(/\{(?:\d+(?:,-?\d+)?(?::[^{}]+)?|[A-Za-z_][\w.-]*)\}|\$\{[^{}]+\}|%(?:\d+\$)?[sdifn]|%\d+|<\/?(?:strong|em|b|i|code|br|a)\b[^>]*>/g)].map(item => item[0]);
  const installerSection = id.match(/^Installer\.(Messages|CustomMessages)\./)?.[1];
  return sorted(installerSection
    ? [...markers.filter(item => !item.startsWith('%')), ...installerMessageTokens(value, installerSection)]
    : markers);
}
function allowEqual(policy, scope, id, value, locale) {
  const exceptions = policy.equalText?.[scope] ?? {};
  const exception = exceptions[id];
  return exception && (exception.value === value || exception.values?.includes(value)) &&
    (!exception.languages || exception.languages.includes(locale)) && Boolean(exception.reason);
}
function sourceResources(root, scope, locale, JSDOM) {
  const file = scope === 'app' ? path.join(root, 'src/DropSpace.App/Strings', locale, 'Resources.resw') : path.join(root, 'website/_source/src/locales', `${locale}.json`);
  const values = scope === 'app' ? resw(file, JSDOM) : json(file);
  if (scope === 'app') {
    checkInstallerScriptOverrides(read(path.join(root, 'installer/DropSpace.iss')));
    const installer = path.join(root, 'installer/localization', `${locale}.isl`);
    let section = '';
    for (const line of read(installer).replace(/^\uFEFF/, '').split(/\r?\n/)) {
      const heading = line.match(/^\[([^\]]+)\]$/);
      if (heading) { section = heading[1]; continue; }
      const item = line.match(/^([^;=][^=]*)=(.*)$/);
      if (item && ['LangOptions','CustomMessages','Messages'].includes(section)) {
        const id = `Installer.${section}.${item[1].trim()}`;
        if (Object.hasOwn(values,id)) throw Error(`${installer}: duplicate '${id}'`);
        values[id] = item[2];
      }
    }
  }
  return { file, values };
}
function resourceChecks(root, scope, manifest, policy, JSDOM, issues) {
  let source;
  try { source = sourceResources(root, scope, manifest.defaultLanguage, JSDOM); }
  catch (error) { issues.push(error.message); return { count: 0, values: {} }; }
  const sourceIds = Object.keys(source.values);
  for (const language of manifest.languages) {
    let translation;
    try { translation = sourceResources(root, scope, language.code, JSDOM); }
    catch (error) { issues.push(`${scope}/${language.code}: ${error.message}`); continue; }
    for (const id of sourceIds) {
      const value = translation.values[id];
      const prefix = `${language.code} ${id} ${id.startsWith('Installer.') ? `installer/localization/${language.code}.isl` : relative(root, translation.file)}`;
      if (typeof value !== 'string' || !value.trim()) { issues.push(`${prefix}: missing/empty translation`); continue; }
      if (!same(placeholders(source.values[id], id), placeholders(value, id))) issues.push(`${prefix}: placeholder/required markup differs from en-US`);
      if (language.code !== manifest.defaultLanguage && value === source.values[id] && !allowEqual(policy, scope, id, value, language.code)) issues.push(`${prefix}: untranslated English; add a narrowly documented invariant only if this is fixed protocol/brand/sample data`);
    }
    for (const id of Object.keys(translation.values)) if (!Object.hasOwn(source.values, id)) issues.push(`${language.code} ${id} ${relative(root, translation.file)}: unknown resource`);
    if (language.code === manifest.defaultLanguage) continue;
    const recordFile = path.join(root, 'localization/reviews', scope, `${language.code}.json`);
    let review;
    try { review = json(recordFile); }
    catch (error) { issues.push(`${scope}/${language.code}: missing/invalid review record ${relative(root, recordFile)} (${error.message})`); continue; }
    for (const id of sourceIds) {
      const record = review.resources?.[id];
      const prefix = `${language.code} ${id} ${id.startsWith('Installer.') ? `installer/localization/${language.code}.isl` : relative(root, translation.file)}`;
      if (!record || !review.reviewedBy || !review.reviewedAt) issues.push(`${prefix}: unconfirmed translation`);
      else if (record[0] !== hash(source.values[id])) issues.push(`${prefix}: source changed; translation pending update or explicit confirmation`);
      else if (record[1] !== hash(translation.values[id] ?? '')) issues.push(`${prefix}: translation changed; review required`);
    }
  }
  const directory = scope === 'app' ? path.join(root, 'src/DropSpace.App/Strings') : path.join(root, 'website/_source/src/locales');
  const actual = fs.readdirSync(directory).filter(name => scope === 'app' ? fs.statSync(path.join(directory, name)).isDirectory() : name.endsWith('.json')).map(name => name.replace(/\.json$/, ''));
  if (!same(actual, manifest.languages.map(item => item.code))) issues.push(`${relative(root, directory)}: directories/resources do not match the shared ten-language manifest`);
  return { count: sourceIds.length, values: source.values };
}
function checkApp(root, manifest, source, policy, issues) {
  const app = path.join(root, 'src/DropSpace.App');
  const core = path.join(root, 'src/DropSpace.Core');
  const settings = read(path.join(core, 'Models/AppSettings.cs'));
  for (const { enumName, enumValue } of [{ enumName: 'System', enumValue: 0 }, ...manifest.languages]) {
    if (!new RegExp(`\\b${enumName}\\s*=\\s*${enumValue}\\b`).test(settings)) issues.push(`AppLanguagePreference.${enumName}: immutable enum value ${enumValue} missing`);
  }
  const catalogPath = path.join(core, 'Policies/AppLanguageCatalog.cs');
  if (!fs.existsSync(catalogPath)) issues.push('AppLanguageCatalog: missing shared language projection');
  else {
    const catalog = read(catalogPath);
    for (const token of ['GetManifestResourceStream("DropSpace.Core.Localization.Languages.json")','JsonDocument.Parse','"enumValue"','"code"','"nativeName"','"traditional"','"simplified"','"languageFallbacks"']) if (!catalog.includes(token)) issues.push(`AppLanguageCatalog: shared manifest projection '${token}' missing`);
    const coreProject = read(path.join(core,'DropSpace.Core.csproj'));
    if (!coreProject.includes('localization\\languages.json') && !coreProject.includes('localization/languages.json')) issues.push('DropSpace.Core.csproj: shared offline language catalog not embedded');
  }
  const project = read(path.join(app, 'DropSpace.App.csproj'));
  for (const required of ['<DefaultLanguage>en-US</DefaultLanguage>', 'Strings\\**\\*.resw', 'GenerateDropSpacePortableResourceIndex', 'BundleDropSpacePortableResourceIndex', 'DropSpace.resources.pri']) if (!project.includes(required)) issues.push(`DropSpace.App.csproj: required offline packaging contract '${required}' missing`);
  const localizer = read(path.join(app, 'Services/ResourceStringLocalizer.cs'));
  if (!localizer.includes('ToResourceMapPath') || !localizer.includes('bracketDepth') || /key\.Replace\('\.',\s*'\/'\)/.test(localizer)) issues.push('ResourceStringLocalizer: preserve dots inside accessibility type qualifiers');
  const manifestText = read(path.join(app, 'Package.appxmanifest'));
  if (!manifestText.includes('DisplayName="ms-resource:AppDisplayName"') || !manifestText.includes('Description="ms-resource:AppDescription"')) issues.push('Package.appxmanifest: display name/description must reference resources');
  const packagedLanguages = [...manifestText.matchAll(/<Resource\s+Language="([^"]+)"/g)].map(item=>item[1]);
  if (!same(packagedLanguages,manifest.languages.map(item=>item.code))) issues.push('Package.appxmanifest: declared languages differ from shared catalog');
  const installer = read(path.join(root,'installer/DropSpace.iss'));
  for (const language of manifest.languages) if (!installer.includes(`localization\\${language.code}.isl`)) issues.push(`installer/DropSpace.iss: ${language.code} wizard language missing`);
  for (const [, id] of installer.matchAll(/\{cm:([^},]+)(?:,[^}]+)?\}/g)) if (!Object.hasOwn(source,`Installer.CustomMessages.${id}`) && !['CreateDesktopIcon','AdditionalIcons','LaunchProgram'].includes(id)) issues.push(`installer/DropSpace.iss: missing CustomMessage '${id}'`);
  for (const match of installer.matchAll(/CustomMessage\('([^']+)'\)/g)) if (!Object.hasOwn(source,`Installer.CustomMessages.${match[1]}`)) issues.push(`installer/DropSpace.iss: missing CustomMessage '${match[1]}'`);
  for (const [expression,literal] of installer.matchAll(/\b(?:Description|GroupDescription|StatusMsg):\s*"([^"{]+)"|\bMsgBox\(\s*'([^']+)'/g)) if (/[\p{L}]/u.test(literal ?? expression) && !(policy.sourceLiterals?.['installer/DropSpace.iss']??[]).some(item=>item.expression===expression && item.reason)) issues.push(`installer/DropSpace.iss: hardcoded installer text '${literal??expression}' bypasses CustomMessages`);
  const installerLanguages = fs.readdirSync(path.join(root,'installer/localization')).filter(item=>item.endsWith('.isl')).map(item=>item.slice(0,-4));
  if (!same(installerLanguages,manifest.languages.map(item=>item.code))) issues.push('installer/localization: language files differ from shared catalog');
  const uids = new Set();
  // Stable messages created outside App retain the same resource completeness
  // contract without expanding the App hardcoded-UI scan into model internals.
  for (const filename of ['src/DropSpace.Core/Models/AppUiMessage.cs', 'src/DropSpace.Infrastructure/Updates/UpdateService.cs']) {
    const file = path.join(root, filename);
    if (!fs.existsSync(file)) continue;
    const content = read(file);
    for (const [, id] of content.matchAll(/\b(?:AppUiMessage\.Resource|strings\.Format)\("([^"]+)"\s*[,)]/g))
      if (!Object.hasOwn(source, id)) issues.push(`${filename}: missing stable message resource reference '${id}'`);
  }
  for (const file of files(app, ['.cs', '.xaml'])) {
    const content = read(file), filename = relative(root, file);
    if (filename.includes('/Diagnostics/')) continue; // Explicit diagnostics are not product UI.
    for (const [, id] of content.matchAll(/\b(?:_strings|strings|localizer|_localizer)\.(?:Get|Format)\("([^"]+)"\s*[,)]/g)) if (!Object.hasOwn(source, id)) issues.push(`${filename}: missing resource reference '${id}'`);
    for (const [, id] of content.matchAll(/\bAppUiMessage\.Resource\("([^"]+)"\s*[,)]/g)) if (!Object.hasOwn(source, id)) issues.push(`${filename}: missing stable message resource reference '${id}'`);
    for (const [, prefix] of content.matchAll(/\b(?:_strings|strings)\.Get\("([^"]+)"\s*\+/g)) if (!['DownloadStage','DownloadState','QqMusicState','LyricsProvider'].includes(prefix) || !Object.keys(source).some(id=>id.startsWith(prefix))) issues.push(`${filename}: undeclared or unbound dynamic resource prefix '${prefix}'`);
    for (const [, uid] of content.matchAll(/XamlResourceOverride\.Uid="([^"]+)"/g)) uids.add(uid);
    if (file.endsWith('.xaml') && /\bx:Uid=/.test(content)) issues.push(`${filename}: use XamlResourceOverride.Uid for unpackaged localization`);
    const matcher = file.endsWith('.xaml') ? /\b(?:Text|Header|Content|PlaceholderText|AutomationProperties\.Name|ToolTipService\.ToolTip)="([^"{]+)"/g : /(?<![\w])(?:Title|Content|Header|Text|PlaceholderText|StatusText|AccessibleName)\s*=\s*"((?:\\.|[^"\\])*)"/g;
    for (const [expression, literal] of content.matchAll(matcher)) {
      if (!/[\p{L}]/u.test(literal) || /^&#/.test(literal)) continue;
      if ((policy.sourceLiterals?.[filename] ?? []).some(item => item.expression === expression && item.reason)) continue;
      issues.push(`${filename}: user-visible hardcoded text '${literal}' bypasses resources`);
    }
    if (file.endsWith('.cs')) {
      for (const [expression,literal] of content.matchAll(/\b(?:AutomationProperties\.(?:SetName|SetHelpText)|ToolTipService\.SetToolTip|(?:ShowToast|ShowNotification|Notify|Notification)\w*)\(\s*(?:[^,\n]+,\s*)?"((?:\\.|[^"\\])*)"/g)) {
        if (!/[\p{L}]/u.test(literal) || (policy.sourceLiterals?.[filename] ?? []).some(item=>item.expression===expression && item.reason)) continue;
        issues.push(`${filename}: accessibility/notification hardcoded text '${literal}' bypasses resources`);
      }
    }
  }
  for (const uid of uids) if (!Object.keys(source).some(id => id.startsWith(`${uid}.`))) issues.push(`XamlResourceOverride '${uid}': no English resource`);
  const selector = read(path.join(app, 'Views/MainPage.xaml.cs'));
  if (!selector.includes('AppLanguageCatalog.All') || !selector.includes('NativeName')) issues.push('MainPage display-language menu: must use shared catalog native names');
  for (const [filename,target,uid,loaded] of [['MainWindow.xaml.cs','AppTitleBar','MainTitleBar',true],['MainWindow.xaml.cs','this','MainWindow',false],['OverlayWindow.xaml.cs','this','OverlayWindow',false]]) {
    const content=read(path.join(app,filename));
    if (!new RegExp(`XamlResourceOverride\\.Apply\\(\\s*${target}\\s*,\\s*"${uid}"\\s*\\)`).test(content)) issues.push(`${filename}: window localization '${uid}' must be applied explicitly`);
    if (loaded && !/AppTitleBar\.Loaded\s*\+=/.test(content)) issues.push(`${filename}: title-bar localization must wait for the native Loaded event`);
  }
  const dynamicEnums=[['DownloadStage','src/DropSpace.Core/Downloads/DownloadModels.cs','DownloadStage'],['DownloadState','src/DropSpace.Core/Downloads/DownloadModels.cs','DownloadTaskState'],['QqMusicState','src/DropSpace.Infrastructure/Lyrics/QqMusicSession.cs','QqMusicSessionState'],['LyricsProvider','src/DropSpace.Core/Models/NativeIslandSettings.cs','LyricsProviderKind']];
  for (const [prefix,file,name] of dynamicEnums) {
    const body=read(path.join(root,file)).match(new RegExp(`enum\\s+${name}\\s*\\{([^}]+)\\}`))?.[1];
    if (!body) { issues.push(`${file}: dynamic resource enum '${name}' missing`); continue; }
    for (const entry of body.replace(/\/\/[^\n]*|\/\*[\s\S]*?\*\//g,'').split(',')) {
      const member=entry.trim().match(/^(\w+)/)?.[1];
      if (member && !Object.hasOwn(source,prefix+member)) issues.push(`${file}: dynamic resource '${prefix+member}' missing`);
    }
  }
}
function checkFrozenLyrics(root, issues) {
  const protocolPath = 'src/DropSpace.Core/Lyrics/PlainHyLyricsProtocol.cs';
  const required = ['public const string EnglishTarget = "英语";', 'public const string ChineseTarget = "简体中文";', 'public const string Template = "将以下文本翻译为{0}，注意只需要输出翻译后的结果，不要额外解释：\\n{1}";'];
  const lines = read(path.join(root, protocolPath)).split(/\r?\n/).map(line => line.trim());
  for (const line of required) if (lines.filter(actual => actual === line).length !== 1) issues.push(`${protocolPath}: frozen model-facing protocol changed`);
  const vocabulary = read(path.join(root, 'src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs')).split(/\r?\n/).map(line => hash(line.trim()));
  for (const expected of ['a1491838c3fff5a19891656102552fa8442759624761e8bf279329f1dd743940', '0ec39eaf32fc5744d3d1e7a2c703249b5dd43e8e256ac525229f611daa429ab7', '1f918a8ce3629dea7b231ec70d21e146dd7c7a656eb527c94fd3fa637a5f16b5']) if (vocabulary.filter(actual => actual === expected).length !== 1) issues.push('LyricsLanguagePolicy.cs: frozen host classification vocabulary changed');
  // Freeze actual latest-main bytes, rather than obsolete historical vocabulary lists.
  for (const [file, expected] of [[protocolPath,'97c6e7645060a31d778a14ef098167ec1a56ee2df83950740cc6ee2554e2803d'],['src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs','802ff6c072323845c4c039dfb07dff7845eae7ca76333f320d3480bbde337be7']]) if (hash(read(path.join(root,file)).replaceAll('\r\n','\n')) !== expected) issues.push(`${file}: protected lyric/model source differs from reviewed main baseline`);
}
function checkWebsite(root, manifest, source, policy, JSDOM, issues) {
  const sourceRoot = path.join(root, 'website/_source/src');
  for (const file of files(sourceRoot, ['.html'])) {
    const document = new JSDOM(read(file)).window.document;
    for (const element of document.querySelectorAll('[data-i18n]')) if (!Object.hasOwn(source, element.dataset.i18n)) issues.push(`${relative(root, file)}: unknown resource '${element.dataset.i18n}'`);
    for (const element of document.querySelectorAll('[data-i18n-attrs]')) {
      let attrs;
      try { attrs = JSON.parse(element.dataset.i18nAttrs); } catch { issues.push(`${relative(root, file)}: invalid data-i18n-attrs JSON`); continue; }
      for (const id of Object.values(attrs)) if (!Object.hasOwn(source, id)) issues.push(`${relative(root, file)}: unknown attribute resource '${id}'`);
    }
    const walker = document.createTreeWalker(document.body, 4);
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const value = node.textContent.trim();
      if (!/[\p{L}]/u.test(value) || /^\{\{[A-Z_]+\}\}$/.test(value)) continue;
      const parent = node.parentElement;
      if (parent.closest('script,style,[data-i18n],[aria-hidden="true"]')) continue;
      const exempt = parent.closest('[data-i18n-exempt]');
      if (exempt?.dataset.i18nExempt === 'release-body' && exempt.getAttribute('lang') === 'en') continue;
      if (exempt?.dataset.i18nExempt === 'sample-lyrics' && ['en', 'zh-CN'].includes(exempt.getAttribute('lang'))) continue;
      if ((policy.websiteLiteralText ?? []).includes(value)) continue;
      issues.push(`${relative(root, file)}: user-visible hardcoded text '${value}' bypasses semantic resources`);
    }
    for (const element of document.querySelectorAll('[aria-label],[title],[placeholder],img[alt]')) {
      const bindings = JSON.parse(element.dataset.i18nAttrs ?? '{}');
      for (const name of ['aria-label','title','placeholder','alt']) {
        const value = element.getAttribute(name);
        if (value && /[\p{L}]/u.test(value) && !bindings[name] && !(policy.websiteLiteralText ?? []).includes(value)) issues.push(`${relative(root, file)}: hardcoded accessibility '${name}=${value}'`);
      }
    }
  }
  for (const file of files(sourceRoot, ['.css'])) for (const [, value] of read(file).matchAll(/\bcontent\s*:\s*["']([^"']+)["']/g)) if (/[\p{L}]/u.test(value) && !(policy.websiteLiteralText ?? []).includes(value)) issues.push(`${relative(root, file)}: CSS generated text '${value}' bypasses resources`);
  for (const file of files(sourceRoot, ['.js'])) {
    const filename = relative(root, file);
    const content=read(file);
    for (const [expression, value] of content.matchAll(/\.(?:textContent|innerText|innerHTML)\s*=\s*["']([^"']+)["']/g)) if (/[\p{L}]/u.test(value) && !(policy.sourceLiterals?.[filename] ?? []).some(item => item.expression === expression && item.reason)) issues.push(`${filename}: script UI '${value}' bypasses resources`);
    for (const [expression, value] of content.matchAll(/\.setAttribute\(\s*["'](?:aria-label|title|placeholder|alt)["']\s*,\s*["']([^"']+)["']/g)) if (/[\p{L}]/u.test(value) && !(policy.sourceLiterals?.[filename] ?? []).some(item=>item.expression===expression&&item.reason)) issues.push(`${filename}: script accessibility '${value}' bypasses resources`);
    for (const [,id] of content.matchAll(/(?:\.t|\bt|\btext)\(\s*["']([^"']+)["']\s*[,)]/g)) if (!Object.hasOwn(source,id)) issues.push(`${filename}: unknown script resource '${id}'`);
  }
}

export async function checkLocalization({ root = defaultRoot, scopes = ['app', 'website'], quiet = false } = {}) {
  const issues = [], manifest = json(path.join(root, 'localization/languages.json')), policy = json(path.join(root, 'localization/policy.json'));
  const expected = ['en-US','zh-CN','zh-TW','ja-JP','ko-KR','de-DE','fr-FR','es-ES','pt-BR','ru-RU'];
  if (!same(manifest.languages.map(item => item.code), expected) || manifest.defaultLanguage !== 'en-US') issues.push('languages.json: exactly ten supported interface languages, English default required');
  if (new Set(manifest.languages.map(item => item.enumValue)).size !== 10) issues.push('languages.json: duplicate App enum value');
  const JSDOM = getDom(root), counts = {};
  for (const scope of scopes) {
    if (!['app', 'website'].includes(scope)) throw Error(`Unknown resource scope ${scope}`);
    const source = resourceChecks(root, scope, manifest, policy, JSDOM, issues); counts[scope] = source.count;
    if (scope === 'app') {
      checkApp(root, manifest, source.values, policy, issues); checkFrozenLyrics(root, issues);
      try { issues.push(...checkModuleResources(path.join(root, 'modules/templates/worker/module.template.json')).map(issue => `modules/templates/worker: ${issue}`)); }
      catch (error) { issues.push(error.message); }
    }
    else checkWebsite(root, manifest, source.values, policy, JSDOM, issues);
  }
  if (issues.length) throw Error(`Localization integrity failed (${issues.length} issues):\n${issues.join('\n')}`);
  if (!quiet) console.log(`Localization integrity passed: ${manifest.languages.length} interface languages; ${Object.entries(counts).map(([scope,count])=>`${scope} ${count} IDs`).join(', ')}. Resource records are not functional test cases; semantic quality is not certified.`);
  return { languages: manifest.languages.length, counts };
}

export async function checkBuiltWebsite(directory, { staticMode = false, root = defaultRoot } = {}) {
  const issues = [], JSDOM = getDom(root), manifest = json(path.join(root,'localization/languages.json'));
  const pages = staticMode ? ['index.html'] : ['index.html', 'changelog/index.html'];
  for (const page of pages) {
    const file = path.join(directory, page);
    if (!fs.existsSync(file)) { issues.push(`${file}: unified English default page missing`); continue; }
    const document = new JSDOM(read(file)).window.document;
    if (!['en', 'en-US'].includes(document.documentElement.lang)) issues.push(`${file}: static/no-script page language must be English`);
    const canonical = document.querySelector('link[rel="canonical"]')?.href;
    if (!canonical || /\/(?:en|zh-cn)\//.test(canonical)) issues.push(`${file}: one unified canonical URL required`);
    if (document.querySelector('link[hreflang]')) issues.push(`${file}: localized alternate URLs are forbidden for unified pages`);
    if (!document.querySelector('[data-language-selector]')) issues.push(`${file}: native-name language selector missing`);
    for (const selector of document.querySelectorAll('[data-language-selector]')) {
      const options = [...selector.querySelectorAll('option')].map(item=>({code:item.value,nativeName:item.textContent}));
      if (JSON.stringify(options) !== JSON.stringify(manifest.languages.map(({code,nativeName})=>({code,nativeName})))) issues.push(`${file}: selector values/native names differ from the shared language manifest`);
    }
    for (const element of document.querySelectorAll('[data-i18n-exempt="release-body"]')) if (element.getAttribute('lang') !== 'en') issues.push(`${file}: release body requires lang=en`);
  }
  const payloadFiles = fs.readdirSync(path.join(directory,'assets')).filter(file=>/^localization-runtime\.[0-9a-f]+\.js$/.test(file));
  if (payloadFiles.length !== 1) issues.push(`${directory}: exactly one complete hashed offline localization runtime is required`);
  else {
    const payloadFile=path.join(directory,'assets',payloadFiles[0]), text=read(payloadFile);
    const match=text.match(/^\s*const \{ catalog, resources \} = (.+);$/m);
    try {
      if (!match) throw Error('built JSON payload missing');
      const payload=JSON.parse(match[1]);
      if (JSON.stringify(payload.catalog)!==JSON.stringify(manifest)) issues.push(`${payloadFile}: bundled language catalog is stale`);
      if (!same(Object.keys(payload.resources),manifest.languages.map(item=>item.code))) issues.push(`${payloadFile}: bundled language resources differ from manifest`);
      for (const language of manifest.languages) {
        const expected=json(path.join(root,'website/_source/src/locales',`${language.code}.json`));
        if (JSON.stringify(payload.resources[language.code])!==JSON.stringify(expected)) issues.push(`${payloadFile}: ${language.code} actual bundled translations differ from reviewed source`);
      }
      const expectedHash=hash(text).slice(0,12);
      if (!payloadFiles[0].includes(`.${expectedHash}.`)) issues.push(`${payloadFile}: immutable filename does not match actual payload bytes`);
    } catch(error) { issues.push(`${payloadFile}: invalid offline localization payload (${error.message})`); }
  }
  for (const legacy of ['en', 'zh-cn']) {
    for (const page of ['index.html', ...(!staticMode ? ['changelog/index.html'] : [])]) {
      const file = path.join(directory, legacy, page);
      if (!fs.existsSync(file)) { issues.push(`${file}: legacy language compatibility entry missing`); continue; }
      const text = read(file);
      if (!text.includes('location.search') || !text.includes('location.hash') || !/(?:setItem|language|locale)/.test(text)) issues.push(`${file}: legacy route must preserve query/hash and explicit link locale`);
    }
  }
  const sitemapFile = path.join(directory, 'sitemap.xml');
  if (fs.existsSync(sitemapFile)) {
    const document = new JSDOM(read(sitemapFile), { contentType: 'application/xml' }).window.document;
    const locations = [...document.querySelectorAll('loc')].map(item => item.textContent);
    if (new Set(locations).size !== locations.length || locations.some(value => /\/(?:en|zh-cn)\//.test(value))) issues.push(`${sitemapFile}: canonical URLs must be unique and locale-independent`);
  }
  if (issues.length) throw Error(`Built website localization failed:\n${issues.join('\n')}`);
  console.log(`Built website integrity passed: ${pages.length} unified page(s), English default, old-link compatibility${staticMode ? ' (static showcase)' : ''}.`);
}

function checkArtifacts(root, directory) {
  const receiptFile = path.join(directory, 'localization-publication.json'), receipt = json(receiptFile), manifest = json(path.join(root, 'localization/languages.json'));
  if (receipt.schemaVersion !== 1 || receipt.manifestSha256 !== hash(fs.readFileSync(path.join(root, 'localization/languages.json')))) throw Error(`${receiptFile}: receipt does not bind the current language manifest`);
  if (receipt.installerScriptSha256 !== hash(fs.readFileSync(path.join(root,'installer/DropSpace.iss')))) throw Error(`${receiptFile}: installer language declarations no longer match compiled input`);
  for (const language of manifest.languages) {
    const file = path.join(root, 'src/DropSpace.App/Strings', language.code, 'Resources.resw');
    if (receipt.sourceSha256?.[language.code] !== hash(fs.readFileSync(file))) throw Error(`${receiptFile}: ${language.code} packaged resources are stale`);
    const installer = path.join(root, 'installer/localization', `${language.code}.isl`);
    if (receipt.installerSourceSha256?.[language.code] !== hash(fs.readFileSync(installer))) throw Error(`${receiptFile}: ${language.code} compiled installer resource binding is stale`);
    if (!Number.isInteger(receipt.resourceCounts?.[language.code]) || receipt.resourceCounts[language.code] <= 0) throw Error(`${receiptFile}: ${language.code} actual packaged resource count missing`);
  }
  if (!receipt.artifacts?.length || !receipt.artifacts.some(item => item.kind === 'portable') || !receipt.artifacts.some(item => item.kind === 'msix')) throw Error(`${receiptFile}: both actual portable and MSIX resource indexes must be verified`);
  for (const item of receipt.artifacts) {
    const file = path.join(directory, item.file);
    if (path.dirname(path.resolve(file)) !== path.resolve(directory) || item.sha256 !== hash(fs.readFileSync(file))) throw Error(`${receiptFile}: artifact hash/path mismatch ${item.file}`);
    if (['portable','msix'].includes(item.kind) && !same(item.languages, manifest.languages.map(language=>language.code))) throw Error(`${receiptFile}: ${item.file} does not contain all ten language resources`);
  }
  console.log(`Packaged localization integrity passed: ${receipt.artifacts.length} bound artifacts; ten offline language resources verified in actual portable/MSIX PRI indexes. No App execution.`);
}

function argument(name, fallback) { const index = process.argv.indexOf(name); return index < 0 ? fallback : process.argv[index + 1]; }
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const root = path.resolve(argument('--root', defaultRoot)), artifactDirectory = argument('--artifacts');
    if (artifactDirectory) checkArtifacts(root, path.resolve(artifactDirectory));
    else if (process.argv.includes('--review') || process.argv.includes('--extract')) {
      const scope = argument('--scope'), locale = argument('--language');
      if (!['app','website'].includes(scope) || !locale || locale === 'en-US') throw Error('--scope app|website and a translated --language are required');
      const JSDOM = getDom(root), source = sourceResources(root, scope, 'en-US', JSDOM), translation = sourceResources(root, scope, locale, JSDOM);
      const file = path.join(root, 'localization/reviews', scope, `${locale}.json`);
      const current = fs.existsSync(file) ? json(file) : { schemaVersion: 1, language: locale, scope, resources: {} };
      if (process.argv.includes('--extract')) {
        const pending = Object.keys(source.values).filter(id => current.resources[id]?.[0] !== hash(source.values[id]) || current.resources[id]?.[1] !== hash(translation.values[id] ?? ''));
        console.log(JSON.stringify({ scope, language: locale, pending: pending.map(id=>({id, source: source.values[id], translation: translation.values[id] ?? null})) },null,2));
      } else {
        const keysFile = argument('--keys-file'), reviewer = argument('--reviewer');
        if (!keysFile || !reviewer) throw Error('Review requires --keys-file with an explicit reviewed ID array and --reviewer; no blanket refresh is supported');
        const keys = json(path.resolve(keysFile)), policy = json(path.join(root, 'localization/policy.json'));
        if (!Array.isArray(keys) || !keys.length || new Set(keys).size !== keys.length) throw Error('--keys-file must list distinct explicitly reviewed IDs');
        for (const id of keys) {
          const original = source.values[id], value = translation.values[id];
          if (typeof original !== 'string' || typeof value !== 'string' || !value.trim() || !same(placeholders(original, id),placeholders(value, id))) throw Error(`${locale}/${id}: missing, empty, unknown or malformed translation cannot be confirmed`);
          if (original === value && !allowEqual(policy,scope,id,value,locale)) throw Error(`${locale}/${id}: copied English cannot be confirmed`);
          current.resources[id] = [hash(original),hash(value)];
        }
        current.reviewedBy = reviewer; current.reviewedAt = new Date().toISOString();
        current.initialReviewMethod ??= reviewer;
        current.latestReviewScope = { ids: keys, reviewedBy: reviewer, reviewedAt: current.reviewedAt };
        fs.mkdirSync(path.dirname(file), { recursive: true }); fs.writeFileSync(file, JSON.stringify(current,null,2)+'\n');
        console.log(`Recorded ${keys.length} explicitly reviewed ${scope}/${locale} IDs. Review records do not certify semantic quality.`);
      }
    } else await checkLocalization({ root, scopes: argument('--scope') ? [argument('--scope')] : undefined });
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
