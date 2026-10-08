// Explicit focused scenario; never included in automatic website test suites.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { JSDOM } from 'jsdom';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const catalog = JSON.parse(fs.readFileSync(path.join(root, 'localization/languages.json'), 'utf8'));
const resources = Object.fromEntries(catalog.languages.map(({ code }) => [code, JSON.parse(fs.readFileSync(path.join(root, 'website/_source/src/locales', code + '.json'), 'utf8'))]));
const runtimeBytes = fs.readFileSync(path.join(root, 'website/_source/src/localization-runtime.js'));
const source = runtimeBytes.toString('utf8').replace('__LOCALIZATION_PAYLOAD__', JSON.stringify({ catalog, resources }));
const storageKey = 'dropspace.interfaceLanguage';
const historyKey = 'dropspace.interfaceLanguageFallback';
const store = { readable: false, writable: false, value: null, writes: 0 };
const ordinaryState = { scrollPosition: 37, other: { keep: 'yes' } };
const address = 'https://airanluo-dot.github.io/DropSpace/?campaign=beta#features';
const originalLink = '/DropSpace/changelog/?source=home#history';
const windows = [];

function openPage(url, state) {
  const dom = new JSDOM('<!doctype html><html data-page="home"><head></head><body><select data-language-selector></select><a href="' + originalLink + '">Changelog</a></body></html>', { url, runScripts: 'outside-only' });
  windows.push(dom.window);
  dom.window.history.replaceState(state, '', url);
  Object.defineProperty(dom.window, 'localStorage', { value: {
    getItem(key) { assert.equal(key, storageKey); if (!store.readable) throw new Error('read denied'); return store.value; },
    setItem(key, value) { assert.equal(key, storageKey); store.writes++; if (!store.writable) throw new Error('write denied'); store.value = value; }
  } });
  Object.defineProperty(dom.window.navigator, 'languages', { value: ['en-US'] });
  dom.window.eval(source);
  dom.window.DropSpaceI18n.apply();
  return dom.window;
}

function selectLanguage(window, code) {
  const selector = window.document.querySelector('[data-language-selector]');
  selector.value = code;
  selector.dispatchEvent(new window.Event('change'));
}

function assertPreservedState(window, choice) {
  assert.equal(window.history.state.scrollPosition, ordinaryState.scrollPosition);
  assert.equal(window.history.state.other.keep, ordinaryState.other.keep);
  if (choice) assert.equal(window.history.state[historyKey], choice);
  else assert.equal(Object.hasOwn(window.history.state, historyKey), false);
}

const startedAtUtc = new Date().toISOString();
let outcome;
try {
  let page = openPage(address, ordinaryState);
  assert.equal(page.DropSpaceI18n.language, 'en-US');
  assert.equal(store.writes, 0, 'automatic browser matching must not become a manual preference');
  selectLanguage(page, 'de-DE');
  assertPreservedState(page, 'de-DE');
  assert.equal(page.location.href, address);
  assert.equal(new URL(page.document.querySelector('a').href).searchParams.get('ds-language'), 'de-DE');

  store.readable = true;
  page = openPage(address, page.history.state);
  assert.equal(page.DropSpaceI18n.language, 'de-DE');
  assertPreservedState(page, 'de-DE');
  assert.equal(store.value, null, 'successful reads cannot stand in for failed writes');
  assert.equal(new URL(page.document.querySelector('a').href).searchParams.get('ds-language'), 'de-DE');

  store.writable = true;
  page = openPage(address, page.history.state);
  assert.equal(store.value, 'de-DE');
  assertPreservedState(page, null);
  assert.equal(page.location.href, address);
  assert.equal(page.document.querySelector('a').getAttribute('href'), originalLink);

  page = openPage(address.replace('?campaign=beta', '?campaign=beta&ds-language=fr-FR'), { ...ordinaryState, [historyKey]: 'de-DE' });
  assert.equal(page.DropSpaceI18n.language, 'fr-FR', 'explicit URL choice must outrank history');
  assert.equal(store.value, 'fr-FR');
  assertPreservedState(page, null);
  assert.equal(page.location.href, address);

  store.readable = false;
  store.writable = false;
  page = openPage(address, ordinaryState);
  selectLanguage(page, 'de-DE');
  assertPreservedState(page, 'de-DE');
  assert.equal(new URL(page.document.querySelector('a').href).searchParams.get('ds-language'), 'de-DE');
  store.writable = true;
  selectLanguage(page, 'fr-FR');
  assert.equal(store.value, 'fr-FR', 'successful writes must recover storage availability even after failed reads');
  assertPreservedState(page, null);
  assert.equal(page.document.querySelector('a').getAttribute('href'), originalLink, 'recovery must restore the original link instead of keeping a stale choice');
  outcome = { scenario: 'manual-language-storage-loss-and-recovery', functionalScenariosExecuted: 1, result: 'passed', startedAtUtc, completedAtUtc: new Date().toISOString(), sourceSha256: createHash('sha256').update(runtimeBytes).digest('hex'), scope: 'Actual localization runtime in JSDOM with controlled read/write failures, refreshes, explicit URL precedence and history/link preservation. No browser rendering, layout, release deployment or App execution.' };
} catch (error) {
  outcome = { scenario: 'manual-language-storage-loss-and-recovery', functionalScenariosExecuted: 1, result: 'failed', startedAtUtc, completedAtUtc: new Date().toISOString(), error: String(error.stack || error) };
  process.exitCode = 1;
} finally {
  for (const window of windows) window.close();
}
console.log(JSON.stringify(outcome, null, 2));
