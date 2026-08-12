import { createHash } from "node:crypto";
import { cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";
import { changelogMeta, site, zh } from "./i18n.mjs";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const src = path.join(root, "src");
const dist = path.join(root, "dist");
const releases = JSON.parse(await readFile(path.join(root, "data/releases.json"), "utf8"));
const stable = releases.stable;
const siteOrigin = (process.env.SITE_ORIGIN ?? "https://airanluo-dot.github.io/DropSpace").replace(/\/$/, "");
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

const assetSources = {
  css: "styles.css",
  js: "script.js",
  logo: "assets/dropspace-logo.png",
  favicon: "assets/favicon.png",
  og: "assets/og-image.png"
};
const assetUrls = {};
for (const [key, relative] of Object.entries(assetSources)) {
  const contents = await readFile(path.join(src, relative));
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

function translateDocument(document, route) {
  if (route !== "zh-cn") return;
  const walker = document.createTreeWalker(document.body, 4);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    if (["SCRIPT", "STYLE"].includes(node.parentElement?.tagName)) continue;
    const value = node.nodeValue;
    const trimmed = value?.trim();
    let translated = zh[trimmed];
    if (trimmed === `Latest Stable · ${stable.tag}`) translated = `最新稳定版 · ${stable.tag}`;
    if (trimmed === `${stable.tag} — ${stable.title}`) translated = `${stable.tag} — ${zh[stable.title] ?? stable.title}`;
    if (trimmed === formatDate(stable.publishedAt, "en-US")) translated = formatDate(stable.publishedAt, "zh-CN");
    if (!trimmed || !translated) continue;
    node.nodeValue = value.replace(trimmed, translated);
  }
  for (const element of document.querySelectorAll("[aria-label], [data-windows], [data-other]")) {
    for (const attribute of ["aria-label", "data-windows", "data-other"]) {
      const value = element.getAttribute(attribute);
      if (value && zh[value]) element.setAttribute(attribute, zh[value]);
    }
  }
}

function releaseEntries(route) {
  const entries = [stable, ...(releases.previews ?? [])];
  return entries.map((release) => {
    const channel = release === stable ? "Stable" : "Preview";
    const title = route === "zh-cn" ? (zh[release.title] ?? release.title) : release.title;
    const notes = (release.summary?.length ? release.summary : [release.title])
      .map((item) => `<li>${escapeHtml(item)}</li>`).join("");
    const languageNote = route === "zh-cn" ? `<p class="release-language-note">${zh["Published release notes remain in their original language."]}</p>` : "";
    return `<article class="release-entry">
      <div class="release-meta"><strong>${escapeHtml(release.tag)}</strong><span>${route === "zh-cn" ? zh[channel] : channel}</span><span>${escapeHtml(formatDate(release.publishedAt, route === "zh-cn" ? "zh-CN" : "en-US"))}</span></div>
      <div class="release-body"><h2>${escapeHtml(title)}</h2><ul>${notes}</ul>${languageNote}<div class="actions"><a class="button button-primary" href="${escapeHtml(release.assets?.installer ?? release.url)}">${route === "zh-cn" ? zh["Download release"] : "Download release"}</a><a class="button button-ghost" href="${escapeHtml(release.url)}" target="_blank" rel="noopener noreferrer">${route === "zh-cn" ? zh["Full release notes"] : "Full release notes"}</a></div></div>
    </article>`;
  }).join("\n");
}

function applyMetadata(document, route, kind) {
  const locale = site[route];
  const pageMeta = kind === "changelog" ? changelogMeta[route] : locale;
  const suffix = kind === "changelog" ? "/changelog/" : "/";
  const canonical = `${siteOrigin}/${route}${suffix}`;
  document.documentElement.lang = locale.lang;
  document.title = pageMeta.title;
  document.querySelector('meta[name="description"]')?.setAttribute("content", pageMeta.description);
  document.querySelector('link[rel="canonical"]')?.setAttribute("href", canonical);
  document.querySelector('meta[property="og:title"]')?.setAttribute("content", pageMeta.title);
  document.querySelector('meta[property="og:description"]')?.setAttribute("content", kind === "changelog" ? pageMeta.description : locale.ogDescription);
  document.querySelector('meta[property="og:url"]')?.setAttribute("content", canonical);
  const publicOrigin = new URL(siteOrigin).origin;
  const publicOg = new URL(assetUrls.og, publicOrigin).href;
  document.querySelector('meta[property="og:image"]')?.setAttribute("content", publicOg);
  document.querySelector('meta[name="twitter:title"]')?.setAttribute("content", pageMeta.title);
  document.querySelector('meta[name="twitter:description"]')?.setAttribute("content", kind === "changelog" ? pageMeta.description : locale.ogDescription);
  document.querySelector('meta[name="twitter:image"]')?.setAttribute("content", publicOg);

  for (const link of document.querySelectorAll('link[rel="alternate"]')) link.remove();
  for (const [hreflang, languageRoute] of [["en", "en"], ["zh-CN", "zh-cn"], ["x-default", "en"]]) {
    const link = document.createElement("link");
    link.rel = "alternate";
    link.hreflang = hreflang;
    link.href = `${siteOrigin}/${languageRoute}${suffix}`;
    document.head.append(link);
  }

  const structured = document.querySelector('script[type="application/ld+json"]');
  if (structured) {
    const data = JSON.parse(structured.textContent);
    data.url = canonical;
    data.inLanguage = locale.lang;
    if (kind === "home") {
      data.softwareVersion = stable.tag;
      data.downloadUrl = stable.assets.installer;
    } else {
      data.name = pageMeta.title;
      data.isPartOf = { "@type": "WebSite", name: "DropSpace", url: `${siteOrigin}/${route}/` };
    }
    structured.textContent = JSON.stringify(data);
    const inlineHash = createHash("sha256").update(structured.textContent).digest("base64");
    const csp = document.createElement("meta");
    csp.httpEquiv = "Content-Security-Policy";
    csp.content = `default-src 'self'; script-src 'self' 'sha256-${inlineHash}'; style-src 'self'; img-src 'self' data:; media-src 'self'; connect-src 'none'; object-src 'none'; base-uri 'self'; form-action 'self'; upgrade-insecure-requests`;
    document.head.prepend(csp);
  }
}

function rewriteLinks(document, route, kind) {
  const home = `${basePath}${route}/`;
  const changelog = `${home}changelog/`;
  for (const element of document.querySelectorAll("[href], [src], [poster]")) {
    for (const attribute of ["href", "src", "poster"]) {
      const value = element.getAttribute(attribute);
      if (!value) continue;
      const basename = value.split("/").pop();
      if (basename === "styles.css") element.setAttribute(attribute, assetUrls.css);
      else if (basename === "script.js") element.setAttribute(attribute, assetUrls.js);
      else if (basename === "dropspace-logo.png") element.setAttribute(attribute, assetUrls.logo);
      else if (basename === "favicon.png") element.setAttribute(attribute, assetUrls.favicon);
      else if (basename === "og-image.png") element.setAttribute(attribute, assetUrls.og);
      else if (basename === "site.webmanifest") element.setAttribute(attribute, `${basePath}site.webmanifest`);
      else if (basename === "product-overview.webp") element.setAttribute(attribute, assetUrls.screenshot);
      else if (basename === "drag-demo.webm") element.setAttribute(attribute, assetUrls.video);
    }
  }
  for (const link of document.querySelectorAll("a[href]")) {
    const value = link.getAttribute("href");
    if (/^(https?:|mailto:|#)/.test(value)) continue;
    if (value.includes("changelog")) link.href = changelog;
    else if (value.includes("index.")) {
      const hashPart = value.includes("#") ? `#${value.split("#")[1]}` : "";
      link.href = `${home}${hashPart}`;
    }
  }
  const switchLink = document.querySelector("[data-language-switch]");
  if (switchLink) {
    const suffix = kind === "changelog" ? "changelog/" : "";
    switchLink.href = `${basePath}${site[route].switchRoute}/${suffix}`;
    switchLink.textContent = site[route].switchLabel;
    switchLink.setAttribute("aria-label", site[route].switchAria);
  }
}

async function render(templatePath, route, kind) {
  let template = replaceTokens(await readFile(path.join(src, templatePath), "utf8"));
  template = template.replace("{{RELEASE_ENTRIES}}", releaseEntries(route));
  const dom = new JSDOM(template);
  const { document } = dom.window;
  translateDocument(document, route);
  rewriteLinks(document, route, kind);
  applyMetadata(document, route, kind);
  return `<!doctype html>\n${document.documentElement.outerHTML}\n`.replace(/^[ \t]+$/gm, "");
}

for (const route of Object.keys(site)) {
  await mkdir(path.join(dist, route, "changelog"), { recursive: true });
  await writeFile(path.join(dist, route, "index.html"), await render("index.html", route, "home"));
  await writeFile(path.join(dist, route, "changelog", "index.html"), await render("changelog/index.html", route, "changelog"));
}

const rootRedirect = `const route=(navigator.languages?.[0]??navigator.language??"").toLowerCase().startsWith("zh")?"zh-cn":"en";location.replace("${basePath}"+route+"/"+location.hash);`;
const rootStyle = `:root{color-scheme:dark}*{box-sizing:border-box}html,body{min-height:100%;margin:0;background:#050506;color:#f7f7f8;font-family:Inter,ui-sans-serif,system-ui,-apple-system,"Segoe UI",sans-serif}body{display:grid;place-items:center;padding:24px}main{width:min(420px,100%);text-align:center}img{width:88px;height:88px;border-radius:22px}h1{margin:22px 0 8px;font-size:34px}p{color:#a8a8b0;line-height:1.6}nav{display:flex;justify-content:center;gap:10px;margin-top:24px}a{border:1px solid #303038;border-radius:999px;padding:10px 16px;color:#fff;text-decoration:none;background:#141416}a:focus-visible{outline:3px solid #a98cff;outline-offset:3px}`;
const rootScriptHash = createHash("sha256").update(rootRedirect).digest("base64");
const rootStyleHash = createHash("sha256").update(rootStyle).digest("base64");
await writeFile(path.join(dist, "index.html"), `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex"><meta name="theme-color" content="#050506"><meta http-equiv="Content-Security-Policy" content="default-src 'self'; script-src 'sha256-${rootScriptHash}'; style-src 'sha256-${rootStyleHash}'; img-src 'self'; object-src 'none'; base-uri 'self'; form-action 'none'"><title>DropSpace</title><link rel="canonical" href="${siteOrigin}/en/"><style>${rootStyle}</style><script>${rootRedirect}</script></head><body><main><img src="${assetUrls.logo}" width="88" height="88" alt="DropSpace logo"><h1>DropSpace</h1><p>Choose your language · 选择语言</p><nav aria-label="Language"><a href="${basePath}en/">English</a><a href="${basePath}zh-cn/">简体中文</a></nav></main></body></html>\n`);
await writeFile(path.join(dist, "404.html"), `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="robots" content="noindex"><title>Page not found — DropSpace</title><link rel="icon" href="${assetUrls.favicon}"><link rel="stylesheet" href="${assetUrls.css}"></head><body><main class="not-found shell"><div><img src="${assetUrls.logo}" width="96" height="96" alt="DropSpace logo"><p class="section-index">404</p><h1>Nothing dropped here.</h1><p>This page is not in Temporary Space.</p><a class="button button-primary" href="${basePath}en/">Back to DropSpace</a></div></main></body></html>\n`);
await writeFile(path.join(dist, "release-data.json"), `${JSON.stringify(releases, null, 2)}\n`);
await writeFile(path.join(dist, "robots.txt"), `User-agent: *\nAllow: /\nSitemap: ${siteOrigin}/sitemap.xml\n`);
await writeFile(path.join(dist, "sitemap.xml"), `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9"><url><loc>${siteOrigin}/en/</loc></url><url><loc>${siteOrigin}/zh-cn/</loc></url><url><loc>${siteOrigin}/en/changelog/</loc></url><url><loc>${siteOrigin}/zh-cn/changelog/</loc></url></urlset>\n`);
const manifest = JSON.parse(await readFile(path.join(src, "site.webmanifest"), "utf8"));
manifest.start_url = basePath;
manifest.icons = [{ src: assetUrls.favicon, sizes: "256x256", type: "image/png" }];
manifest.version = stable.tag;
await writeFile(path.join(dist, "site.webmanifest"), `${JSON.stringify(manifest)}\n`);
console.log(`Built atomic bilingual DropSpace website for ${stable.tag}.`);
