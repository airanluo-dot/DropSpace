// Built with the shared language catalog and all offline website resources.
(() => {
  if (window.DropSpaceI18n) { window.DropSpaceI18n.apply(); return; }
  const { catalog, resources } = __LOCALIZATION_PAYLOAD__;
  const storageKey = 'dropspace.interfaceLanguage';
  const historyKey = 'dropspace.interfaceLanguageFallback';
  const root = document.documentElement;
  const supported = new Map(catalog.languages.map(language => [language.code.toLowerCase(), language.code]));
  function matchLanguage(value) {
    const tag = String(value || '').replaceAll('_', '-').toLowerCase();
    if (supported.has(tag)) return supported.get(tag);
    const parts = tag.split('-'), language = parts[0];
    if (language === 'zh') {
      const chinese = catalog.matching.chinese;
      const aliases = [['zh-TW', chinese.traditional], ['zh-CN', chinese.simplified]];
      const script = parts.find(part => part.length === 4);
      if (script) for (const [code, tags] of aliases) if (tags.some(alias => alias.toLowerCase().split('-').includes(script))) return code;
      for (const [code, tags] of aliases) if (tags.some(alias => alias.toLowerCase().split('-').slice(1).some(part => parts.includes(part)))) return code;
      return chinese.default;
    }
    return catalog.matching.languageFallbacks[language] || null;
  }
  function preferredLanguage(preferences) {
    // Match each preferred language in order: exact tag, then script/region fallback.
    for (const preference of preferences || []) { const match = matchLanguage(preference); if (match) return match; }
    return catalog.defaultLanguage;
  }
  const parameters = new URLSearchParams(location.search);
  const linkedLanguage = supported.get((parameters.get('ds-language') || '').toLowerCase());
  let saved, storageAvailable = true;
  try { saved = supported.get((localStorage.getItem(storageKey) || '').toLowerCase()); } catch { storageAvailable = false; }
  let pageChoice;
  try { pageChoice = supported.get(String(history.state?.[historyKey] || '').toLowerCase()); } catch { /* Browser preferences remain available. */ }
  let language = linkedLanguage || pageChoice || saved || preferredLanguage(navigator.languages?.length ? navigator.languages : [navigator.language]);
  function rememberLanguage(choice) {
    try { localStorage.setItem(storageKey, choice); } catch { storageAvailable = false; }
    try {
      const previous = history.state;
      // Preserve other page state; opaque states keep the existing navigation fallback.
      if (previous !== null && (typeof previous !== 'object' || Array.isArray(previous))) return;
      if (storageAvailable && !Object.hasOwn(previous || {}, historyKey)) return;
      const next = { ...(previous || {}) };
      if (storageAvailable) delete next[historyKey]; else next[historyKey] = choice;
      history.replaceState(next, '', location.href);
    } catch { /* Internal links still carry the choice when browser state is unavailable. */ }
  }
  // Recover a manual choice from history when storage becomes writable again.
  // Persist before internal links are rendered without their temporary language hints.
  if (pageChoice && storageAvailable && !linkedLanguage) rememberLanguage(pageChoice);
  if (linkedLanguage) {
    rememberLanguage(linkedLanguage);
    parameters.delete('ds-language');
    try { history.replaceState(history.state, '', location.pathname + (parameters.size ? '?' + parameters.toString() : '') + location.hash); } catch { /* The page remains usable. */ }
  }
  function t(key, args = {}) {
    const value = resources[language]?.[key] || resources[catalog.defaultLanguage][key] || key;
    return value.replace(/\{([A-Za-z][A-Za-z0-9]*)\}/g, (placeholder, name) => Object.hasOwn(args, name) ? String(args[name]) : placeholder);
  }
  function apply() {
    root.lang = language;
    root.dataset.language = language;
    for (const element of document.querySelectorAll('[data-i18n]')) {
      let args = {}; try { args = JSON.parse(element.dataset.i18nArgs || '{}'); } catch { /* Validated build markup supplies ordinary JSON. */ }
      element.textContent = t(element.dataset.i18n, args);
    }
    for (const element of document.querySelectorAll('[data-i18n-attrs]')) {
      const attributes = JSON.parse(element.dataset.i18nAttrs);
      for (const [attribute, key] of Object.entries(attributes)) element.setAttribute(attribute, t(key));
    }
    for (const selector of document.querySelectorAll('[data-language-selector]')) {
      if (!selector.options.length) for (const item of catalog.languages) {
        const option = document.createElement('option'); option.value = item.code; option.textContent = item.nativeName; selector.append(option);
      }
      selector.value = language;
      if (!selector.dataset.initialized) {
        selector.dataset.initialized = 'true';
        selector.addEventListener('change', () => {
          language = supported.get(selector.value.toLowerCase()) || catalog.defaultLanguage;
          rememberLanguage(language);
          apply();
        });
      }
    }
    if (!storageAvailable) for (const link of document.querySelectorAll('a[href]')) {
      const destination = new URL(link.getAttribute('href'), location.href);
      if (destination.origin !== location.origin || destination.pathname === location.pathname || !/\/(?:index\.html|changelog\/)?$/.test(destination.pathname)) continue;
      destination.searchParams.set('ds-language', language);
      link.href = destination.pathname + destination.search + destination.hash;
    }
    const kind = root.dataset.page || 'home';
    document.title = t(kind === 'error' ? 'error.title' : 'meta.' + kind + '.title');
    const description = t(kind === 'error' ? 'error.description' : 'meta.' + kind + '.description');
    for (const attribute of ['meta[name="description"]','meta[property="og:description"]','meta[name="twitter:description"]']) document.querySelector(attribute)?.setAttribute('content', description);
    for (const attribute of ['meta[property="og:title"]','meta[name="twitter:title"]']) document.querySelector(attribute)?.setAttribute('content', document.title);
    const check = document.querySelector('[data-system-check]');
    if (check?.dataset.result) check.textContent = check.dataset[check.dataset.result === 'windows' ? 'windows' : 'other'];
    for (const node of document.querySelectorAll('[data-localized-date]')) node.textContent = new Intl.DateTimeFormat(language, {dateStyle:'long', timeZone:'UTC'}).format(new Date(node.dataset.localizedDate));
    root.removeAttribute('data-i18n-pending');
    dispatchEvent(new CustomEvent('dropspace-language-change', {detail:{language}}));
  }
  window.DropSpaceI18n = { t, apply, matchLanguage, preferredLanguage, get language() { return language; } };
  root.lang = language;
  if (language !== catalog.defaultLanguage) root.dataset.i18nPending = 'true';
  // A blocked resource or interrupted navigation never leaves a hidden document.
  setTimeout(() => root.removeAttribute('data-i18n-pending'), 1500);
  document.addEventListener('DOMContentLoaded', apply, {once:true});
})();
