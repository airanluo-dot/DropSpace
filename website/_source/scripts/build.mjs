import { createHash } from "node:crypto";
import { mkdir, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";
import { languageCatalog, resources, text } from "./i18n.mjs";
import { checkLocalization, checkBuiltWebsite } from "../../../scripts/check-localization.mjs";
import { createLatestChangeApi, validateWebsiteReleaseData } from "./release-contract.mjs";
import { releaseRequirementsHint, stableRequirements } from "../src/release-requirements.js";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
await checkLocalization({ root: path.resolve(root, "../.."), scopes: ["website"] });
const src = path.join(root, "src");
const staticShowcase = process.env.SITE_VARIANT === "static";
const dist = path.join(root, staticShowcase ? "dist-static" : "dist");
const releases = validateWebsiteReleaseData(JSON.parse(await readFile(path.join(root, "data/releases.json"), "utf8")));
const stable = releases.stable;
const stableRelease = releases.api.releases.find((release) => release.tagName === stable.tag);
const latestChange = createLatestChangeApi(releases.api);
const siteOrigin = (process.env.SITE_ORIGIN ?? (staticShowcase ? "https://dropspace-static.arenvox.chatgpt.site" : "https://airanluo-dot.github.io/DropSpace")).replace(/\/$/, "");
const basePath = new URL(`${siteOrigin}/`).pathname;

const escapeHtml = (value = "") => String(value)
  .replaceAll("&", "&amp;")
  .replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;")
  .replaceAll('"', "&quot;")
  .replaceAll("'", "&#39;");

const hash = (contents) => createHash("sha256").update(contents).digest("hex").slice(0, 12);
const formatDate = (value, locale) => new Intl.DateTimeFormat(locale, {
  dateStyle: "long",
  timeZone: "UTC"
}).format(new Date(value));

await rm(dist, { recursive: true, force: true });
await mkdir(path.join(dist, "assets"), { recursive: true });
if (!staticShowcase) await mkdir(path.join(dist, "api", "v1"), { recursive: true });

const assetSources = {
  css: "styles.css",
  requirementsJs: "release-requirements.js",
  localizationJs: "localization-runtime.js",
  js: "script.js",
  lyricsJs: "lyrics-demo.js",
  lyricsAudio: "assets/lyrics-demo.wav",
  logo: "assets/dropspace-logo.png",
  favicon: "assets/favicon.png",
  og: "assets/og-image.png"
};
const assetUrls = {};
for (const [key, relative] of Object.entries(assetSources)) {
  let contents = await readFile(path.join(src, relative));
  if (key === "localizationJs") contents = Buffer.from(contents.toString().replace("__LOCALIZATION_PAYLOAD__", JSON.stringify({ catalog: languageCatalog, resources })));
  if (staticShowcase && key === "js") contents = Buffer.from(contents.toString().replace(/\/\/ BEGIN LIVE RELEASE RUNTIME[\s\S]*?\/\/ END LIVE RELEASE RUNTIME/g, ""));
  if (key === "js") contents = Buffer.from(contents.toString().replace('"./release-requirements.js"', JSON.stringify(assetUrls.requirementsJs)));
  const extension = path.extname(relative);
  const stem = path.basename(relative, extension);
  const outputName = `${stem}.${hash(contents)}${extension}`;
  await writeFile(path.join(dist, "assets", outputName), contents);
  assetUrls[key] = `${basePath}assets/${outputName}`;
}

for (const [key, relative] of Object.entries({
  screenshot: "assets/product-overview.webp",
  video: "assets/drag-demo.webm"
})) {
  try {
    const contents = await readFile(path.join(src, relative));
    const extension = path.extname(relative);
    const stem = path.basename(relative, extension);
    const outputName = `${stem}.${hash(contents)}${extension}`;
    await writeFile(path.join(dist, "assets", outputName), contents);
    assetUrls[key] = `${basePath}assets/${outputName}`;
  } catch {
    assetUrls[key] = assetUrls.og;
  }
}

const replacements = {
  "{{STABLE_TAG}}": stable.tag,
  "{{STABLE_TITLE}}": stable.title,
  "{{STABLE_DATE}}": formatDate(stable.publishedAt, "en-US"),
  "{{STABLE_URL}}": stable.url,
  "{{INSTALLER_URL}}": stable.assets.installer,
  "{{PORTABLE_URL}}": stable.assets.portable,
  "{{MSIX_URL}}": stable.assets.msix,
  "{{CHECKSUM_URL}}": stable.assets.checksums
};

function replaceTokens(value) {
  let result = value;
  for (const [token, replacement] of Object.entries(replacements)) {
    result = result.split(token).join(replacement ?? stable.url);
  }
  return result;
}

function releaseEntries() {
  // Static and runtime presentations use the same validated release API, never an old locale branch.
  return releases.api.releases.map((release) => {
    const channel = release.isPrerelease ? "release.beta" : "release.stable";
    const notes = release.body.split("\n").map(line => line.trim()).filter(line => /^[-*] /.test(line)).slice(0, 5)
      .map(line => `<li>${escapeHtml(line.replace(/^[-*] +/, "").replace(/[*_`]/g, ""))}</li>`).join("");
    const installer = release.assets.find(asset => asset.name === "DropSpaceSetup.exe")?.downloadUrl || release.htmlUrl;
    return `<article class="release-entry" data-release-tag="${escapeHtml(release.tagName)}">
      <div class="release-meta"><strong>${escapeHtml(release.tagName)}</strong><span data-i18n="${channel}">${text(channel)}</span><span data-localized-date="${escapeHtml(release.publishedAt)}">${escapeHtml(formatDate(release.publishedAt, "en-US"))}</span></div>
      <div class="release-body"><div lang="en" data-i18n-exempt="release-body"><h2>${escapeHtml(release.name || release.tagName)}</h2><ul>${notes}</ul></div><div class="actions"><a class="button button-primary" href="${escapeHtml(installer)}" data-i18n="release.download">${text("release.download")}</a><a class="button button-ghost" href="${escapeHtml(release.htmlUrl)}" target="_blank" rel="noopener noreferrer" data-i18n="release.notes">${text("release.notes")}</a></div></div>
    </article>`;
  }).join("\n");
}

function applyMetadata(document, kind) {
  const pageMeta = { title: text(`meta.${kind}.title`), description: text(`meta.${kind}.description`) };
  const canonical = `${siteOrigin}/${kind === "changelog" ? "changelog/" : staticShowcase ? "index.html" : ""}`;
  document.documentElement.lang = "en-US";
  document.documentElement.dataset.page = kind;
  document.title = pageMeta.title;
  document.querySelector('meta[name="description"]')?.setAttribute("content", pageMeta.description);
  document.querySelector('link[rel="canonical"]')?.setAttribute("href", canonical);
  document.querySelector('meta[property="og:title"]')?.setAttribute("content", pageMeta.title);
  document.querySelector('meta[property="og:description"]')?.setAttribute("content", kind === "changelog" ? pageMeta.description : text("meta.home.ogDescription"));
  document.querySelector('meta[property="og:url"]')?.setAttribute("content", canonical);
  const publicOrigin = new URL(siteOrigin).origin;
  const publicOg = new URL(assetUrls.og, publicOrigin).href;
  document.querySelector('meta[property="og:image"]')?.setAttribute("content", publicOg);
  document.querySelector('meta[name="twitter:title"]')?.setAttribute("content", pageMeta.title);
  document.querySelector('meta[name="twitter:description"]')?.setAttribute("content", kind === "changelog" ? pageMeta.description : text("meta.home.ogDescription"));
  document.querySelector('meta[name="twitter:image"]')?.setAttribute("content", publicOg);

  for (const link of document.querySelectorAll('link[rel="alternate"]')) link.remove();
  const structured = document.querySelector('script[type="application/ld+json"]');
  if (structured) {
    const data = JSON.parse(structured.textContent);
    data.url = canonical;
    data.inLanguage = "en-US";
    if (kind === 'home') {
      if (!staticShowcase) data.softwareVersion = stable.tag;
      else delete data.softwareVersion;
      data.downloadUrl = staticShowcase ? 'https://github.com/airanluo-dot/DropSpace/releases/latest/download/DropSpaceSetup.exe' : stable.assets.installer;
    } else {
      data.name = pageMeta.title;
      data.isPartOf = {'@type':'WebSite',name:'DropSpace',url:siteOrigin+'/'};
    }
    structured.textContent = JSON.stringify(data);
    const inlineHash = createHash('sha256').update(structured.textContent).digest('base64');
    const csp = document.createElement('meta'); csp.httpEquiv = 'Content-Security-Policy';
    csp.content = `default-src 'self'; script-src 'self' 'sha256-${inlineHash}'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'self' https://airanluo-dot.github.io; object-src 'none'; base-uri 'self'; form-action 'self'; upgrade-insecure-requests`;
    document.head.prepend(csp);
  }
}

function rewriteLinks(document) {
  const home = basePath + (staticShowcase ? 'index.html' : '');
  const changelog = basePath + 'changelog/';
  const sources = {...assetSources, screenshot:'assets/product-overview.webp',video:'assets/drag-demo.webm'};
  for (const element of document.querySelectorAll('[href],[src],[poster]')) {
    for (const attribute of ['href','src','poster']) {
      const value = element.getAttribute(attribute); if (!value) continue;
      const basename = value.split('/').pop();
      const asset = Object.entries(sources).find(([,source]) => path.basename(source) === basename);
      if (asset) element.setAttribute(attribute, assetUrls[asset[0]]);
      else if (basename === 'site.webmanifest') element.setAttribute(attribute, basePath + 'site.webmanifest');
    }
  }
  for (const link of document.querySelectorAll('a[href]')) {
    const value = link.getAttribute('href');
    if (/^(https?:|mailto:|#)/.test(value)) continue;
    if (value.includes('changelog')) link.href = changelog;
    else if (value.includes('index.')) link.href = home + (value.includes('#') ? '#' + value.split('#')[1] : '');
  }
}

async function render(templatePath, kind) {
  const change = latestChange.release;
  let template = replaceTokens(await readFile(path.join(src,templatePath),'utf8'))
    .replace('{{RELEASE_ENTRIES}}',releaseEntries())
    .replace('{{LATEST_CHANGE_HEADLINE}}',escapeHtml(change.headline.en))
    .replace('{{LATEST_CHANGE_TAG}}',escapeHtml(change.tagName))
    .replace('{{LATEST_CHANGE_TITLE}}',escapeHtml(change.title))
    .replace('{{LATEST_CHANGE_DATE}}',escapeHtml(formatDate(change.publishedAt,'en-US')))
    .replace('{{LATEST_CHANGE_URL}}',escapeHtml(change.htmlUrl))
    .replace('{{LATEST_CHANGE_HIGHLIGHTS}}',change.highlights.en.map(item => `<span>${escapeHtml(item)}</span>`).join(''));
  const {document} = new JSDOM(template).window;
  for (const selector of document.querySelectorAll('[data-language-selector]')) for (const item of languageCatalog.languages) {
    const option = document.createElement('option'); option.value = item.code; option.textContent = item.nativeName; selector.append(option);
  }
  for (const node of document.querySelectorAll('[data-i18n]')) node.textContent = text(node.dataset.i18n, JSON.parse(replaceTokens(node.dataset.i18nArgs || '{}')));
  document.querySelectorAll('[data-stable-requirements]').forEach(node => {
    node.textContent = staticShowcase ? releaseRequirementsHint('en-US',text) : stableRequirements(stableRelease,'en-US',text);
    if (!staticShowcase) node.dataset.requirementRelease = JSON.stringify({tagName:stableRelease.tagName,body:stableRelease.body});
  });
  rewriteLinks(document);
  const localizationHead = document.createElement('script'); localizationHead.src = assetUrls.localizationJs; document.head.append(localizationHead);
  const localizationBody = document.createElement('script'); localizationBody.src = assetUrls.localizationJs; document.body.insertBefore(localizationBody, document.body.querySelector('script'));
  if (staticShowcase) {
    document.documentElement.dataset.siteVariant = 'static';
    document.querySelectorAll('.stable-line,[data-stable-version],[data-latest-change]').forEach(node => node.remove());
    const artifacts = {installer:'DropSpaceSetup.exe',portable:'DropSpace.exe',msix:'DropSpace-x64.msix',checksums:'SHA256SUMS.txt'};
    for (const link of document.querySelectorAll('a[href]')) {
      if (link.hasAttribute('data-download')) link.href = `https://github.com/airanluo-dot/DropSpace/releases/latest/download/${artifacts[link.dataset.download]}`;
      else if (link.hasAttribute('data-release-url') || link.href.includes('/changelog/')) link.href = 'https://github.com/airanluo-dot/DropSpace/releases';
    }
  }
  applyMetadata(document, kind);
  return '<!doctype html>\n' + document.documentElement.outerHTML + '\n';
}

await writeFile(path.join(dist, "index.html"), await render("index.html", "home"));
if (!staticShowcase) {
  await mkdir(path.join(dist, "changelog"), { recursive: true });
  await writeFile(path.join(dist, "changelog", "index.html"), await render("changelog/index.html", "changelog"));
}

// Compatibility documents preserve the old page, selected language, query and fragment.
for (const [route, language] of [["en", "en-US"], ["zh-cn", "zh-CN"]]) {
  for (const kind of ["home", "changelog"]) {
    const target = staticShowcase && kind === "changelog" ? "https://github.com/airanluo-dot/DropSpace/releases" : `${basePath}${kind === "changelog" ? "changelog/" : staticShowcase ? "index.html" : ""}`;
    const redirect = `const target=${JSON.stringify(target)};const query=new URLSearchParams(location.search);query.set("ds-language",${JSON.stringify(language)});try{localStorage.setItem("dropspace.interfaceLanguage",${JSON.stringify(language)})}catch{}location.replace(target+"?"+query.toString()+location.hash);`;
    const scriptHash = createHash("sha256").update(redirect).digest("base64");
    const folder = path.join(dist, route, ...(kind === "changelog" ? ["changelog"] : []));
    await mkdir(folder, { recursive: true });
    await writeFile(path.join(folder, "index.html"), `<!doctype html><html lang="${language}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex"><link rel="canonical" href="${staticShowcase && kind === "changelog" ? target : siteOrigin + '/' + (kind === "changelog" ? 'changelog/' : staticShowcase ? 'index.html' : '')}"><meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'sha256-${scriptHash}'; object-src 'none'; base-uri 'self'"><title>DropSpace</title><script>${redirect}</script></head><body><p><a href="${target}?ds-language=${language}">${language === 'en-US' ? 'Continue to DropSpace' : '继续访问 DropSpace'}</a></p></body></html>\n`);
  }
}
const missing = new JSDOM(`<!doctype html><html lang="en-US" data-page="error"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex"><title>${text('error.title')}</title><link rel="icon" href="${assetUrls.favicon}"><link rel="stylesheet" href="${assetUrls.css}"></head><body><main class="not-found shell"><div><img src="${assetUrls.logo}" width="96" height="96" alt="DropSpace"><p class="section-index">404</p><h1 data-i18n="error.heading">${text('error.heading')}</h1><p data-i18n="error.description">${text('error.description')}</p><a class="button button-primary" data-i18n="error.home" href="${basePath}${staticShowcase ? 'index.html' : ''}">${text('error.home')}</a></div></main><script src="${assetUrls.localizationJs}"></script></body></html>`);
await writeFile(path.join(dist, "404.html"), '<!doctype html>\n' + missing.window.document.documentElement.outerHTML + '\n');
if (!staticShowcase) await writeFile(path.join(dist, "release-data.json"), `${JSON.stringify(releases, null, 2)}\n`);
if (!staticShowcase) await writeFile(path.join(dist, "api", "v1", "releases.json"), `${JSON.stringify(releases.api, null, 2)}\n`);
if (!staticShowcase) await writeFile(path.join(dist, "api", "v1", "latest-change.json"), `${JSON.stringify(latestChange, null, 2)}\n`);
await writeFile(path.join(dist, "robots.txt"), `User-agent: *\nAllow: /\nSitemap: ${siteOrigin}/sitemap.xml\n`);
await writeFile(path.join(dist, "sitemap.xml"), `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>${siteOrigin}/${staticShowcase ? 'index.html' : ''}</loc></url>${staticShowcase ? '' : `<url><loc>${siteOrigin}/changelog/</loc></url>`}</urlset>\n`);
const manifest = JSON.parse(await readFile(path.join(src, "site.webmanifest"), "utf8"));
manifest.start_url = basePath;
manifest.icons = [{ src: assetUrls.favicon, sizes: "256x256", type: "image/png" }];
if (!staticShowcase) manifest.version = stable.tag;
else delete manifest.version;
await writeFile(path.join(dist, "site.webmanifest"), `${JSON.stringify(manifest)}\n`);
await checkBuiltWebsite(dist, { staticMode: staticShowcase });
console.log(staticShowcase ? "Built ten-language static DropSpace showcase at one address." : `Built atomic ten-language DropSpace website for ${stable.tag}.`);
