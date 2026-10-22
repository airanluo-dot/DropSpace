export function suggestedPlatform(navigatorLike) {
  const platform = navigatorLike.userAgentData?.platform || navigatorLike.platform || "";
  if (/mac/i.test(platform) && (navigatorLike.maxTouchPoints || 0) <= 1) return "macos";
  if (/win/i.test(platform)) return "windows";
  return null;
}

const escapeHtml = (value = "") => String(value).replaceAll("&", "&amp;").replaceAll("<", "&lt;")
  .replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&#39;");
const macPrefix = "https://github.com/airanluo-dot/DropSpace/releases/";
const macNames = { installer: "DropSpace-macOS-arm64.dmg", archive: "DropSpace-macOS-arm64.zip" };
function compareMacTags(left, right) {
  const version = tag => tag.slice(7).split(/\.|-beta\./).map(Number);
  const a = version(left), b = version(right);
  for (let i = 0; i < 4; i++) {
    const difference = (a[i] ?? Number.MAX_SAFE_INTEGER) - (b[i] ?? Number.MAX_SAFE_INTEGER);
    if (difference) return difference;
  }
  return 0;
}

export function validMacFeed(feed) {
  if (feed?.schemaVersion !== 1 || feed.platform !== "macos" || !Number.isFinite(Date.parse(feed.generatedAt)) ||
      !Array.isArray(feed.releases) || feed.releases.length > 20) return false;
  const seen = new Set();
  for (const entry of feed.releases) {
    const m = entry?.manifest;
    if (!/^macos-v\d+\.\d+\.\d+(?:-beta\.[1-9]\d*)?$/.test(m?.tag ?? "") || seen.has(m.tag) ||
        m.schemaVersion !== 1 || m.platform !== "macos" || m.architecture !== "arm64" ||
        m.channel !== (m.tag.includes("-beta.") ? "beta" : "stable") ||
        !/^\d+\.\d+(?:\.\d+)?$/.test(m.minimumOS ?? "") || Number(m.minimumOS.split(".")[0]) < 26 ||
        m.releaseURL !== `${macPrefix}tag/${m.tag}` || typeof m.releaseNotes !== "string" || m.releaseNotes.length > 65536 ||
        !Number.isFinite(Date.parse(entry.publishedAt)) || entry.checksumsURL !== `${macPrefix}download/${m.tag}/SHA256SUMS-macos.txt`) return false;
    for (const [kind, name] of Object.entries(macNames)) {
      const asset = m[kind];
      if (asset?.name !== name || asset.url !== `${macPrefix}download/${m.tag}/${name}` ||
          !Number.isSafeInteger(asset.size) || asset.size <= 0 || asset.size > 2 * 1024 ** 3 ||
          !/^[a-f0-9]{64}$/.test(asset.sha256 ?? "")) return false;
    }
    seen.add(m.tag);
  }
  const releases = [...feed.releases].sort((a, b) => compareMacTags(b.manifest.tag, a.manifest.tag));
  return JSON.stringify(feed.stable) === JSON.stringify(releases.find(e => e.manifest.channel === "stable") ?? null) &&
    JSON.stringify(feed.beta) === JSON.stringify(releases[0] ?? null);
}

export function macReleaseMarkup(entry, t) {
  if (!entry) return `<p data-i18n="mac.unreleased">${escapeHtml(t("mac.unreleased"))}</p>`;
  const m = entry.manifest;
  const link = (key, url, style = "") => `<a class="${style}" href="${escapeHtml(url)}" data-i18n="${key}">${escapeHtml(t(key))}</a>`;
  return `<p class="stable-line" data-mac-tag="${escapeHtml(m.tag)}">${escapeHtml(m.tag)}</p>
    <div class="actions">${link("mac.download", m.installer.url, "button button-primary")}${link("mac.archive", m.archive.url, "button button-ghost")}</div>
    <div class="mac-release-links">${link("release.notes", m.releaseURL)}${link("ui.sha.256.checksums", entry.checksumsURL)}</div>`;
}

export function macHistoryMarkup(entries, t, formatDate) {
  const highlights = notes => {
    const bullets = notes.split(/\r?\n/).map(line => line.trim()).filter(line => /^[-*]\s+/.test(line)).slice(0, 6);
    if (bullets.length) return `<ul>${bullets.map(line => `<li>${escapeHtml(line.replace(/^[-*]\s+/, '').replace(/\*\*|`/g, ''))}</li>`).join('')}</ul>`;
    return `<p>${escapeHtml(notes.split(/\r?\n/).find(line => line.trim() && !line.startsWith('#')) ?? '')}</p>`;
  };
  return entries.map(({manifest: m, publishedAt}) => `<article class="release-entry">
    <div class="release-meta"><strong>${escapeHtml(m.tag)}</strong><time datetime="${escapeHtml(publishedAt)}">${escapeHtml(formatDate(publishedAt))}</time></div>
    <div class="release-body"><div lang="en" data-i18n-exempt="release-body">${highlights(m.releaseNotes)}</div><div class="actions">
    <a class="button button-primary" href="${escapeHtml(m.installer.url)}" data-i18n="mac.download">${escapeHtml(t("mac.download"))}</a>
    <a class="button button-ghost" href="${escapeHtml(m.releaseURL)}" data-i18n="release.notes">${escapeHtml(t("release.notes"))}</a></div></div></article>`).join("");
}

async function refreshMacRelease() {
  const download = document.querySelector("[data-mac-release]");
  if (!download || document.documentElement.dataset.siteVariant === "static") return;
  document.documentElement.dataset.macReleaseApi = "build-snapshot";
  try {
    const response = await fetch(new URL("../api/macos/v1/releases.json", import.meta.url),
      { headers: { Accept: "application/json" }, cache: "no-cache", signal: AbortSignal.timeout(6000) });
    if (!response.ok) return;
    const feed = await response.json();
    if (!validMacFeed(feed)) return;
    const selected = feed.stable ?? feed.beta;
    const current = download.querySelector("[data-mac-tag]")?.dataset.macTag;
    if (!selected || (current && compareMacTags(selected.manifest.tag, current) < 0)) return;
    const t = key => window.DropSpaceI18n.t(key);
    download.innerHTML = macReleaseMarkup(selected, t);
    document.querySelector("[data-mac-history]").innerHTML = macHistoryMarkup(feed.releases, t,
      date => new Date(date).toLocaleDateString(window.DropSpaceI18n.language, { dateStyle: "long", timeZone: "UTC" }));
    for (const requirements of document.querySelectorAll("[data-mac-requirements]"))
      requirements.dataset.i18nArgs = JSON.stringify({ version: selected.manifest.minimumOS });
    window.DropSpaceI18n.apply();
    document.documentElement.dataset.macReleaseApi = "current";
  } catch { /* The validated build snapshot remains usable offline. */ }
}

if (typeof document !== "undefined") {
  void refreshMacRelease();
  addEventListener("dropspace-language-change", () => {
    for (const time of document.querySelectorAll("[data-mac-history] time[datetime]"))
      time.textContent = new Date(time.dateTime).toLocaleDateString(window.DropSpaceI18n.language, { dateStyle: "long", timeZone: "UTC" });
  });
  const platform = suggestedPlatform(navigator);
  document.documentElement.dataset.suggestedPlatform = platform || "unknown";
  for (const link of document.querySelectorAll("[data-platform-option]")) {
    if (link.dataset.platformOption === platform) link.dataset.suggested = "true";
  }
  // A suggestion never hides the other platform or claims CPU/OS compatibility.
  if (platform === "macos") {
    for (const link of document.querySelectorAll("[data-platform-primary]")) {
      link.href = link.dataset.macUrl;
      link.removeAttribute("data-download");
      link.replaceChildren();
      const label = document.createElement("span"); label.dataset.i18n = "mac.nav";
      label.textContent = window.DropSpaceI18n?.t("mac.nav") || "Mac version";
      link.append(label);
    }
    window.DropSpaceI18n?.apply();
  }
}
