const header = document.querySelector("[data-header]");
const demo = document.querySelector("[data-demo]");
const reduced = matchMedia("(prefers-reduced-motion: reduce)");

addEventListener("scroll", () => header?.classList.toggle("scrolled", scrollY > 18), { passive: true });

if (demo) {
  let timer;
  const run = () => {
    if (reduced.matches) {
      demo.dataset.state = "expanded";
      return;
    }
    const states = [["idle", 0], ["ready", 2500], ["expanded", 3900], ["idle", 6500]];
    let index = 0;
    const step = () => {
      demo.dataset.state = states[index][0];
      const next = states[(index + 1) % states.length];
      const delay = index === states.length - 1 ? 700 : next[1] - states[index][1];
      index = (index + 1) % states.length;
      timer = setTimeout(step, delay);
    };
    step();
  };
  run();
  reduced.addEventListener("change", () => { clearTimeout(timer); run(); });
}

const observer = new IntersectionObserver((entries) => {
  for (const entry of entries) if (entry.isIntersecting) entry.target.classList.add("revealed");
}, { threshold: 0.12 });
document.querySelectorAll("main section").forEach((section) => observer.observe(section));

const systemCheck = document.querySelector("[data-system-check]");
if (systemCheck) {
  const platform = navigator.userAgentData?.platform ?? navigator.platform ?? "";
  const isWindows = /windows|win32|win64/i.test(platform);
  systemCheck.textContent = isWindows ? systemCheck.dataset.windows : systemCheck.dataset.other;
  systemCheck.dataset.result = isWindows ? "windows" : "other";
}

// Release data is refreshed from the versioned GitHub Pages contract. The generated page already
// contains the last successfully deployed, validated snapshot; failed production syncs are not deployed.
const isGitHubPages = location.hostname.endsWith("github.io");
const isLocalPreview = ["localhost", "127.0.0.1", "::1"].includes(location.hostname);
const releaseApiPaths = isGitHubPages
  ? [`/${location.pathname.split("/").filter(Boolean)[0]}/api/v1/releases.json`]
  : isLocalPreview
    ? ["/api/v1/releases.json"]
    : ["/api/v1/releases.json", "https://airanluo-dot.github.io/DropSpace/api/v1/releases.json"];
const latestChangeApiPaths = releaseApiPaths.map((path) => path.replace(/releases\.json$/, "latest-change.json"));

void Promise.all([refreshReleaseData(), refreshLatestChangeData()]);

async function refreshReleaseData() {
  const responses = await Promise.allSettled(releaseApiPaths.map(async (endpoint) => {
    try {
      const response = await fetch(endpoint, {
        headers: { Accept: "application/json" },
        cache: "no-cache",
        signal: AbortSignal.timeout(6000)
      });
      if (!response.ok) return [];
      const payload = await response.json();
      if (payload?.schemaVersion !== 1 || !Array.isArray(payload.releases) || payload.releases.length > 20) return [];
      const releases = payload.releases.filter(isValidRelease);
      return releases.length === payload.releases.length ? releases : [];
    } catch {
      return [];
    }
  }));
  const merged = new Map();
  for (const result of responses) {
    if (result.status !== "fulfilled") continue;
    for (const release of result.value) merged.set(release.tagName, release);
  }
  const releases = [...merged.values()].sort((left, right) =>
    String(right.publishedAt ?? "").localeCompare(String(left.publishedAt ?? "")));
  if (releases.length > 0) {
    applyCurrentReleases(releases);
    document.documentElement.dataset.releaseApi = "current";
    return;
  }
  // The last successfully deployed snapshot remains usable when a runtime refresh is unavailable.
  document.documentElement.dataset.releaseApi = "build-snapshot";
}

function isValidRelease(release) {
  if (!/^v\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(release?.tagName ?? "")) return false;
  if (release.htmlUrl !== `https://github.com/airanluo-dot/DropSpace/releases/tag/${release.tagName}`) return false;
  const names = new Set();
  const kinds = new Set();
  return Array.isArray(release.assets) && release.assets.every((asset) =>
    Number.isSafeInteger(asset.size) && asset.size > 0 &&
    (asset.kind === null || asset.kind === undefined ||
      ["installer", "portable", "msix", "checksums", "manifest"].includes(asset.kind)) &&
    (asset.kind === null || asset.kind === undefined ||
      (!kinds.has(asset.kind) && kinds.add(asset.kind))) &&
    !names.has(asset.name) && names.add(asset.name) &&
    asset.downloadUrl === `https://github.com/airanluo-dot/DropSpace/releases/download/${release.tagName}/${asset.name}`);
}

async function refreshLatestChangeData() {
  for (const endpoint of latestChangeApiPaths) {
    try {
      const response = await fetch(endpoint, {
        headers: { Accept: "application/json" },
        cache: "no-cache",
        signal: AbortSignal.timeout(6000)
      });
      if (!response.ok) continue;
      const payload = await response.json();
      if (!isValidLatestChange(payload)) continue;
      applyLatestChange(payload.release);
      document.documentElement.dataset.latestChangeApi = "current";
      return;
    } catch {
      // Try the next official replica and retain the validated build snapshot on failure.
    }
  }
  document.documentElement.dataset.latestChangeApi = "build-snapshot";
}

function isValidLatestChange(payload) {
  const release = payload?.release;
  if (payload?.schemaVersion !== 1 || payload.source !== "github-releases" ||
      !Number.isFinite(Date.parse(payload.generatedAt)) ||
      !/^v\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(release?.tagName ?? "") ||
      release.htmlUrl !== `https://github.com/airanluo-dot/DropSpace/releases/tag/${release.tagName}` ||
      !Number.isFinite(Date.parse(release.publishedAt)) ||
      release.channel !== (release.tagName.includes("-preview.") ? "preview" : "stable") ||
      !isBoundedText(release.title, 160)) return false;
  return ["en", "zh-CN"].every((locale) =>
    isBoundedText(release.headline?.[locale], 80) &&
    Array.isArray(release.highlights?.[locale]) &&
    release.highlights[locale].length >= 1 && release.highlights[locale].length <= 6 &&
    release.highlights[locale].every((item) => isBoundedText(item, 500)));
}

function isBoundedText(value, maxLength) {
  return typeof value === "string" && value.length >= 1 && value.length <= maxLength &&
    value.trim() === value && !/[\u0000-\u001f]/.test(value);
}

function applyLatestChange(release) {
  const locale = document.documentElement.lang.toLowerCase().startsWith("zh") ? "zh-CN" : "en";
  const setText = (selector, value) => document.querySelectorAll(selector).forEach((node) => { node.textContent = value; });
  setText("[data-latest-change-headline]", release.headline[locale]);
  setText("[data-latest-change-tag]", release.tagName);
  setText("[data-latest-change-title]", release.title);
  setText("[data-latest-change-date]", new Date(release.publishedAt).toLocaleDateString(locale, { dateStyle: "long", timeZone: "UTC" }));
  document.querySelectorAll("[data-latest-change-url]").forEach((link) => { link.href = release.htmlUrl; });

  const container = document.querySelector("[data-latest-change-highlights]");
  if (!container) return;
  const fragment = document.createDocumentFragment();
  for (const value of release.highlights[locale]) {
    const item = document.createElement("span");
    item.textContent = value;
    fragment.append(item);
  }
  container.replaceChildren(fragment);
}

function applyCurrentReleases(releases) {
  const stable = releases.find((release) => !release.isDraft && !release.isPrerelease);
  if (!stable) return;
  const assets = Object.fromEntries(
    stable.assets
      .filter((asset) => typeof asset.kind === "string")
      .map((asset) => [asset.kind, asset.downloadUrl]));
  for (const kind of ["installer", "portable", "msix", "checksums"]) {
    for (const link of document.querySelectorAll(`[data-download="${kind}"]`)) {
      if (assets[kind]) link.href = assets[kind];
    }
  }
  document.querySelectorAll("[data-release-url]").forEach((link) => { link.href = stable.htmlUrl; });
  document.querySelectorAll("[data-stable-tag]").forEach((node) => { node.textContent = stable.tagName; });
  const zh = document.documentElement.lang.toLowerCase().startsWith("zh");
  document.querySelectorAll("[data-stable-version]").forEach((node) => {
    node.textContent = `${zh ? "最新稳定版" : "Latest Stable"} · ${stable.tagName}`;
  });

  const container = document.querySelector("[data-release-entries]");
  if (container) renderReleaseEntries(container, releases.filter((release) => !release.isDraft), zh);
}

function renderReleaseEntries(container, releases, zh) {
  const fragment = document.createDocumentFragment();
  for (const release of releases) {
    const article = document.createElement("article");
    article.className = "release-entry";
    const meta = document.createElement("div");
    meta.className = "release-meta";
    for (const value of [release.tagName, release.isPrerelease ? "Preview" : "Stable", new Date(release.publishedAt).toLocaleDateString(zh ? "zh-CN" : "en-US", { dateStyle: "long", timeZone: "UTC" })]) {
      const node = document.createElement(meta.childElementCount ? "span" : "strong");
      node.textContent = value;
      meta.append(node);
    }
    const body = document.createElement("div");
    body.className = "release-body";
    const title = document.createElement("h2");
    title.textContent = release.name || release.tagName;
    const list = document.createElement("ul");
    release.body.split("\n").map((line) => line.trim()).filter((line) => /^[-*] /.test(line)).slice(0, 5).forEach((line) => {
      const item = document.createElement("li");
      item.textContent = line.replace(/^[-*] +/, "").replace(/[*_`]/g, "");
      list.append(item);
    });
    const actions = document.createElement("div");
    actions.className = "actions";
    const installer = release.assets.find((asset) => asset.kind === "installer");
    for (const [label, href, className] of [
      [zh ? "下载版本" : "Download release", installer?.downloadUrl ?? release.htmlUrl, "button button-primary"],
      [zh ? "完整发布说明" : "Full release notes", release.htmlUrl, "button button-ghost"]
    ]) {
      const link = document.createElement("a");
      link.className = className;
      link.href = href;
      link.textContent = label;
      if (href === release.htmlUrl) {
        link.target = "_blank";
        link.rel = "noopener noreferrer";
      }
      actions.append(link);
    }
    body.append(title, list, actions);
    article.append(meta, body);
    fragment.append(article);
  }
  container.replaceChildren(fragment);
}
