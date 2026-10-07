import { stableRequirements } from "./release-requirements.js";

const i18n = window.DropSpaceI18n;
i18n.apply();
function applyRequirementSnapshot() {
  document.querySelectorAll("[data-stable-requirements]").forEach(node => {
    if (node.dataset.requirementRelease) node.textContent = stableRequirements(JSON.parse(node.dataset.requirementRelease), i18n.language);
    else node.textContent = i18n.t("requirements.hint");
  });
}
applyRequirementSnapshot();
addEventListener("dropspace-language-change", applyRequirementSnapshot);
const header = document.querySelector("[data-header]");
const demo = document.querySelector("[data-demo]");
const reduced = matchMedia("(prefers-reduced-motion: reduce)");
const releaseArtifacts = Object.freeze({
  installer: "DropSpaceSetup.exe",
  portable: "DropSpace.exe",
  msix: "DropSpace-x64.msix",
  checksums: "SHA256SUMS.txt",
  manifest: "update-manifest.json"
});

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

// BEGIN LIVE RELEASE RUNTIME
// Release data is refreshed from the versioned GitHub Pages contract. The generated page already
// contains the last successfully deployed, validated snapshot; failed production syncs are not deployed.
const isGitHubPages = location.hostname.endsWith("github.io");
const isLocalPreview = ["localhost", "127.0.0.1", "::1"].includes(location.hostname);
const releaseApiPaths = isGitHubPages
  ? [`/${location.pathname.split("/").filter(Boolean)[0]}/api/v1/releases.json`]
  : isLocalPreview
    ? ["/api/v1/releases.json"]
    : ["/api/v1/releases.json", "https://airanluo-dot.github.io/DropSpace/api/v1/releases.json"];
let currentStable;
addEventListener("dropspace-language-change", () => { if (currentStable) updateRequirementText(currentStable); });
function updateRequirementText(stable) { document.querySelectorAll("[data-stable-requirements]").forEach(node => { node.textContent = stableRequirements(stable, i18n.language); }); }
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
      if (payload?.schemaVersion !== 1 || payload.source !== "github-releases" ||
          typeof payload.generatedAt !== "string" || !Number.isFinite(Date.parse(payload.generatedAt)) ||
          !Array.isArray(payload.releases) || payload.releases.length > 20) return [];
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
  const releases = [...merged.values()].sort((left, right) => Date.parse(right.publishedAt) - Date.parse(left.publishedAt));
  if (releases.length > 0 && applyCurrentReleases(releases)) {
    document.documentElement.dataset.releaseApi = "current";
    return;
  }
  // The last successfully deployed snapshot remains usable when a runtime refresh is unavailable.
  document.documentElement.dataset.releaseApi = "build-snapshot";
}

function isValidRelease(release) {
  if (typeof release?.tagName !== "string" ||
      !/^v\d+\.\d+\.\d+(?:-(?:preview|beta)\.\d+)?$/.test(release.tagName) ||
      typeof release.name !== "string" || typeof release.body !== "string" || release.isDraft !== false ||
      typeof release.isPrerelease !== "boolean" ||
      typeof release.publishedAt !== "string" || !Number.isFinite(Date.parse(release.publishedAt))) return false;
  if (release.htmlUrl !== `https://github.com/airanluo-dot/DropSpace/releases/tag/${release.tagName}`) return false;
  const names = new Set();
  return Array.isArray(release.assets) && release.assets.every((asset) =>
    typeof asset?.name === "string" && asset.name.length > 0 &&
    Number.isSafeInteger(asset.size) && asset.size > 0 &&
    (asset.kind === null || asset.kind === undefined ||
      (Object.hasOwn(releaseArtifacts, asset.kind) && releaseArtifacts[asset.kind] === asset.name)) &&
    !names.has(asset.name) && names.add(asset.name) &&
    asset.downloadUrl === `https://github.com/airanluo-dot/DropSpace/releases/download/${release.tagName}/${asset.name}`);
}

function compareReleaseVersions(leftTag, rightTag) {
  const pattern = /^v(\d+)\.(\d+)\.(\d+)(?:-(?:preview|beta)\.(\d+))?$/;
  const left = pattern.exec(leftTag);
  const right = pattern.exec(rightTag);
  for (let index = 1; index <= 3; index++) {
    const a = BigInt(left[index]), b = BigInt(right[index]);
    if (a !== b) return a > b ? 1 : -1;
  }
  if (left[4] === undefined || right[4] === undefined) {
    return left[4] === right[4] ? 0 : left[4] === undefined ? 1 : -1;
  }
  const a = BigInt(left[4]), b = BigInt(right[4]);
  return a === b ? 0 : a > b ? 1 : -1;
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
      !/^v\d+\.\d+\.\d+(?:-(?:preview|beta)\.\d+)?$/.test(release?.tagName ?? "") ||
      release.htmlUrl !== `https://github.com/airanluo-dot/DropSpace/releases/tag/${release.tagName}` ||
      !Number.isFinite(Date.parse(release.publishedAt)) ||
      (release.channel === "preview" ? "beta" : release.channel) !== (/-(?:preview|beta)\./.test(release.tagName) ? "beta" : "stable") ||
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
  const setText = (selector, value) => document.querySelectorAll(selector).forEach((node) => { node.textContent = value; });
  setText("[data-latest-change-headline]", release.headline.en);
  setText("[data-latest-change-tag]", release.tagName);
  setText("[data-latest-change-title]", release.title);
  setText("[data-latest-change-date]", new Date(release.publishedAt).toLocaleDateString("en-US", { dateStyle: "long", timeZone: "UTC" }));
  document.querySelectorAll("[data-latest-change-url]").forEach((link) => { link.href = release.htmlUrl; });

  const container = document.querySelector("[data-latest-change-highlights]");
  if (!container) return;
  const fragment = document.createDocumentFragment();
  for (const value of release.highlights.en) {
    const item = document.createElement("span");
    item.textContent = value;
    fragment.append(item);
  }
  container.replaceChildren(fragment);
}

function applyCurrentReleases(releases) {
  const stable = releases.filter((release) => !release.isDraft && !release.isPrerelease)
    .sort((left, right) => compareReleaseVersions(right.tagName, left.tagName))[0];
  if (!stable) return false;
  currentStable = stable;
  const assets = Object.fromEntries(
    Object.entries(releaseArtifacts).map(([kind, name]) =>
      [kind, stable.assets.find((asset) => asset.name === name)?.downloadUrl]));
  if (Object.values(assets).some((url) => !url)) return false;
  for (const kind of ["installer", "portable", "msix", "checksums"]) {
    for (const link of document.querySelectorAll(`[data-download="${kind}"]`)) {
      if (assets[kind]) link.href = assets[kind];
    }
  }
  document.querySelectorAll("[data-release-url]").forEach((link) => { link.href = stable.htmlUrl; });
  document.querySelectorAll("[data-stable-tag]").forEach((node) => { node.textContent = stable.tagName; });
  document.querySelectorAll("[data-stable-version]").forEach((node) => {
    node.dataset.i18nArgs = JSON.stringify({tag:stable.tagName});
    node.textContent = i18n.t("release.latestStable", {tag:stable.tagName});
  });
  updateRequirementText(stable);

  const container = document.querySelector("[data-release-entries]");
  if (container) renderReleaseEntries(container, releases.filter((release) => !release.isDraft));
  return true;
}

function renderReleaseEntries(container, releases) {
  const fragment = document.createDocumentFragment();
  for (const release of releases) {
    const article = document.createElement("article");
    article.className = "release-entry";
    const meta = document.createElement("div");
    meta.className = "release-meta";
    for (const value of [release.tagName, i18n.t(release.isPrerelease ? "release.beta" : "release.stable"), new Date(release.publishedAt).toLocaleDateString(i18n.language, { dateStyle: "long", timeZone: "UTC" })]) {
      const node = document.createElement(meta.childElementCount ? "span" : "strong");
      node.textContent = value;
      if (meta.childElementCount === 1) node.dataset.i18n = release.isPrerelease ? "release.beta" : "release.stable";
      if (meta.childElementCount === 2) node.dataset.localizedDate = release.publishedAt;
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
    const installer = release.assets.find((asset) => asset.name === releaseArtifacts.installer);
    for (const [label, href, className] of [
      ["release.download", installer?.downloadUrl ?? release.htmlUrl, "button button-primary"],
      ["release.notes", release.htmlUrl, "button button-ghost"]
    ]) {
      const link = document.createElement("a");
      link.className = className;
      link.href = href;
      link.dataset.i18n = label;
      link.textContent = i18n.t(label);
      if (href === release.htmlUrl) {
        link.target = "_blank";
        link.rel = "noopener noreferrer";
      }
      actions.append(link);
    }
    const releaseText = document.createElement("div"); releaseText.lang = "en"; releaseText.dataset.i18nExempt = "release-body"; releaseText.append(title, list);
    body.append(releaseText, actions);
    article.append(meta, body);
    fragment.append(article);
  }
  container.replaceChildren(fragment);
}

// END LIVE RELEASE RUNTIME

// Explicit, keyboard-accessible exploration; never auto-rotate or play audio.
for (const showcase of document.querySelectorAll('[data-native-showcase]')) {
  const tabs = [...showcase.querySelectorAll('[role="tab"]')];
  const select = (tab, focus = false) => {
    for (const item of tabs) {
      const active = item === tab;
      item.setAttribute('aria-selected', String(active));
      item.tabIndex = active ? 0 : -1;
      document.getElementById(item.getAttribute('aria-controls')).hidden = !active;
    }
    if (focus) tab.focus();
  };
  for (const [index, tab] of tabs.entries()) {
    tab.addEventListener('click', () => select(tab));
    tab.addEventListener('keydown', (event) => {
      let target;
      if (event.key === 'ArrowRight') target = Math.min(tabs.length - 1, index + 1);
      if (event.key === 'ArrowLeft') target = Math.max(0, index - 1);
      if (event.key === 'Home') target = 0;
      if (event.key === 'End') target = tabs.length - 1;
      if (target === undefined) return;
      event.preventDefault();
      select(tabs[target], true);
    });
  }
}
