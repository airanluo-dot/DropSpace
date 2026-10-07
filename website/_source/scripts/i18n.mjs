import { readFile } from 'node:fs/promises';

// The website and App share supported languages, matching rules and product terms.
export const languageCatalog = JSON.parse(await readFile(new URL('../../../localization/languages.json', import.meta.url), 'utf8'));
export const resources = Object.fromEntries(await Promise.all(languageCatalog.languages.map(async ({code}) => [
  code, JSON.parse(await readFile(new URL(`../src/locales/${code}.json`, import.meta.url), 'utf8'))
])));
export function text(key, args = {}, language = 'en-US') {
  const value = resources[language]?.[key] || resources['en-US'][key];
  if (!value) throw new Error(`Missing website resource ${language}:${key}`);
  return value.replace(/\{([A-Za-z][A-Za-z0-9]*)\}/g, (placeholder, name) => Object.hasOwn(args, name) ? String(args[name]) : placeholder);
}
