import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const hash = value => crypto.createHash('sha256').update(value, 'utf8').digest('hex');
const placeholders = value => [...value.matchAll(/(?<!\{)\{\d+(?:,[^}:]+)?(?::[^}]+)?\}(?!\})/g)]
  .map(match => match[0]).sort();

export function checkModuleResources(manifestFile, reviewFile = path.join(path.dirname(manifestFile), 'localization-reviews.json')) {
  const issues = [];
  const manifest = JSON.parse(fs.readFileSync(manifestFile, 'utf8').replace(/^\uFEFF/, ''));
  const review = JSON.parse(fs.readFileSync(reviewFile, 'utf8').replace(/^\uFEFF/, ''));
  const languages = JSON.parse(fs.readFileSync(path.join(root, 'localization/languages.json'), 'utf8')).languages.map(item => item.code);
  const source = manifest.Resources?.['en-US'];
  if (!source || review.schemaVersion !== 1 || review.moduleId !== manifest.Id) return ['Invalid module resource/review identity'];
  const sourceKeys = Object.keys(source).sort();
  if (sourceKeys.length < 1 || sourceKeys.length > 256) issues.push('Invalid module resource count');
  if (JSON.stringify(Object.keys(manifest.Resources).sort()) !== JSON.stringify([...languages].sort()))
    issues.push('Module resources must contain exactly the ten supported interface languages');
  for (const language of languages) {
    const translated = manifest.Resources[language];
    if (!translated || JSON.stringify(Object.keys(translated).sort()) !== JSON.stringify(sourceKeys)) {
      issues.push(`${language}: resource keys differ from English`); continue;
    }
    for (const key of sourceKeys) {
      const english = source[key], value = translated[key], confirmed = review.languages?.[language]?.[key];
      if (typeof english !== 'string' || typeof value !== 'string' || !english.trim() || !value.trim()) {
        issues.push(`${language}:${key}: empty or invalid resource`); continue;
      }
      if (JSON.stringify(placeholders(english)) !== JSON.stringify(placeholders(value)))
        issues.push(`${language}:${key}: placeholder mismatch`);
      if (!confirmed?.reviewer?.trim() || !confirmed?.confirmedAt ||
          confirmed.sourceSha256 !== hash(english) || confirmed.translationSha256 !== hash(value))
        issues.push(`${language}:${key}: missing or stale explicit translation review`);
    }
  }
  const refs = [manifest.Name, ...manifest.Ui.Pages.flatMap(page =>
    [page.Title, page.Description, ...page.Actions.map(action => action.Label)]), ...manifest.Ui.Settings.map(setting => setting.Label)];
  for (const text of refs) if (!Object.hasOwn(source, text?.Key)) issues.push(`Missing declaration resource: ${text?.Key}`);
  return issues;
}

const invoked = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
if (invoked) {
  const manifestIndex = process.argv.indexOf('--manifest');
  if (manifestIndex < 0 || !process.argv[manifestIndex + 1]) {
    console.error('Usage: node modules/check-module-resources.mjs --manifest <module.template.json> [--review --language <code> --keys-file <explicit JSON key array> --reviewer <identity/scope>]');
    process.exitCode = 2;
  } else {
    try {
      const manifest = path.resolve(process.argv[manifestIndex + 1]);
      if (process.argv.includes('--review')) {
        const option = name => process.argv[process.argv.indexOf(name) + 1];
        for (const name of ['--language', '--keys-file', '--reviewer'])
          if (!process.argv.includes(name) || !option(name)?.trim()) throw new Error(`${name} is required for explicit review`);
        const declaration = JSON.parse(fs.readFileSync(manifest, 'utf8'));
        const language = option('--language');
        const keys = JSON.parse(fs.readFileSync(path.resolve(option('--keys-file')), 'utf8'));
        if (!Array.isArray(keys) || !keys.length || new Set(keys).size !== keys.length ||
            !declaration.Resources[language] || keys.some(key => typeof key !== 'string' || !Object.hasOwn(declaration.Resources['en-US'], key)))
          throw new Error('Review requires explicit existing unique resource keys and a supported language');
        const reviewFile = path.join(path.dirname(manifest), 'localization-reviews.json');
        const review = fs.existsSync(reviewFile) ? JSON.parse(fs.readFileSync(reviewFile, 'utf8')) :
          { schemaVersion: 1, moduleId: declaration.Id, languages: {} };
        if (review.schemaVersion !== 1 || review.moduleId !== declaration.Id) throw new Error('Review module identity mismatch');
        review.languages[language] ??= {};
        const confirmedAt = new Date().toISOString();
        for (const key of keys) {
          const source = declaration.Resources['en-US'][key], translated = declaration.Resources[language][key];
          if (typeof translated !== 'string' || !translated.trim() || language !== 'en-US' && translated === source)
            throw new Error(`${language}:${key}: missing translation or unapproved English copy`);
          review.languages[language][key] = { sourceSha256: hash(source), translationSha256: hash(translated),
            reviewer: option('--reviewer'), confirmedAt };
        }
        fs.writeFileSync(reviewFile, JSON.stringify(review, null, 2) + '\n');
      }
      const issues = checkModuleResources(manifest);
      if (issues.length) { console.error(issues.join('\n')); process.exitCode = 1; }
      else console.log('Module resources: ten languages, exact keys/placeholders and confirmed fingerprints.');
    } catch (error) { console.error(error.message); process.exitCode = 1; }
  }
}
