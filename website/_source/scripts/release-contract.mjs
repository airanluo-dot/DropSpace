export const RELEASE_API_SCHEMA_VERSION = 1;
export const RELEASE_API_MAX_ITEMS = 20;

export const REQUIRED_RELEASE_ASSETS = Object.freeze([
  "DropSpaceSetup.exe",
  "DropSpace.exe",
  "DropSpace-x64.msix",
  "SHA256SUMS.txt",
  "update-manifest.json"
]);

const OFFICIAL_DOWNLOAD = /^https:\/\/github\.com\/airanluo-dot\/DropSpace\/releases\/download\/([^/]+)\/([^/?#]+)$/;
const OFFICIAL_RELEASE = /^https:\/\/github\.com\/airanluo-dot\/DropSpace\/releases\/tag\/([^/?#]+)$/;

export function normalizeGitHubReleases(releases, generatedAt = new Date().toISOString()) {
  if (!Array.isArray(releases)) throw new TypeError("GitHub release payload must be an array.");
  const normalized = releases
    .filter((release) => !release.draft)
    .slice(0, RELEASE_API_MAX_ITEMS)
    .map((release) => normalizeRelease(release));
  return {
    schemaVersion: RELEASE_API_SCHEMA_VERSION,
    generatedAt,
    source: "github-releases",
    releases: normalized
  };
}

export function validateReleaseApi(payload) {
  if (!payload || payload.schemaVersion !== RELEASE_API_SCHEMA_VERSION || !Array.isArray(payload.releases)) {
    throw new TypeError("Unsupported DropSpace release API payload.");
  }
  if (payload.source !== "github-releases" || !isIsoDate(payload.generatedAt)) {
    throw new TypeError("Invalid DropSpace release API provenance.");
  }
  if (payload.releases.length > RELEASE_API_MAX_ITEMS) throw new RangeError("Too many releases.");
  for (const release of payload.releases) {
    if (!/^v\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(release.tagName ?? "")) throw new TypeError("Invalid release tag.");
    if (typeof release.name !== "string" || typeof release.body !== "string" || release.isDraft !== false ||
        typeof release.isPrerelease !== "boolean" || !isIsoDate(release.publishedAt)) {
      throw new TypeError("Invalid release metadata.");
    }
    const releaseMatch = OFFICIAL_RELEASE.exec(release.htmlUrl ?? "");
    if (!releaseMatch || decodeURIComponent(releaseMatch[1]) !== release.tagName) throw new TypeError("Invalid release URL.");
    if (!Array.isArray(release.assets)) throw new TypeError("Invalid release assets.");
    const names = new Set();
    for (const asset of release.assets) {
      if (!asset.name || !Number.isSafeInteger(asset.size) || asset.size <= 0) throw new TypeError("Invalid release asset.");
      if (names.has(asset.name)) throw new TypeError("Duplicate release asset.");
      names.add(asset.name);
      const match = OFFICIAL_DOWNLOAD.exec(asset.downloadUrl ?? "");
      if (!match || decodeURIComponent(match[1]) !== release.tagName || decodeURIComponent(match[2]) !== asset.name) {
        throw new TypeError("Release asset is not an official same-release download.");
      }
    }
  }
  return payload;
}

export function createWebsiteReleaseData(githubPayload, generatedAt = new Date().toISOString()) {
  if (!Array.isArray(githubPayload)) throw new TypeError("GitHub release payload must be an array.");
  const releases = githubPayload.filter((release) => !release.draft).slice(0, RELEASE_API_MAX_ITEMS);
  const stable = releases.find((release) => !release.prerelease);
  if (!stable) throw new TypeError("GitHub Releases did not contain a Stable release in the first 20 items.");

  const data = {
    syncedAt: generatedAt,
    source: "github",
    api: normalizeGitHubReleases(releases, generatedAt),
    stable: normalizeWebsiteRelease(stable),
    previews: releases.filter((release) => release.prerelease).slice(0, 5).map(normalizeWebsiteRelease)
  };
  return validateWebsiteReleaseData(data);
}

export function validateWebsiteReleaseData(data) {
  if (!data || data.source !== "github" || !isIsoDate(data.syncedAt)) {
    throw new TypeError("Invalid website release data provenance.");
  }
  validateReleaseApi(data.api);
  if (!data.stable || !Array.isArray(data.previews)) throw new TypeError("Website release data is incomplete.");

  const apiByTag = new Map(data.api.releases.map((release) => [release.tagName, release]));
  validateWebsiteEntry(data.stable, apiByTag, false);
  for (const preview of data.previews) validateWebsiteEntry(preview, apiByTag, true);

  requireReleaseAssets(apiByTag.get(data.stable.tag), "Stable");
  if (data.previews[0]) requireReleaseAssets(apiByTag.get(data.previews[0].tag), "latest Preview");
  return data;
}

function normalizeRelease(release) {
  const tagName = String(release.tag_name ?? "");
  const htmlUrl = String(release.html_url ?? "");
  const assets = (release.assets ?? []).map((asset) => ({
    name: String(asset.name ?? ""),
    size: Number(asset.size),
    downloadUrl: String(asset.browser_download_url ?? "")
  }));
  const normalized = {
    tagName,
    name: String(release.name ?? tagName),
    body: normalizeOfficialWebsiteReferences(String(release.body ?? "")),
    isDraft: false,
    isPrerelease: Boolean(release.prerelease),
    publishedAt: release.published_at ?? null,
    htmlUrl,
    assets
  };
  validateReleaseApi({
    schemaVersion: RELEASE_API_SCHEMA_VERSION,
    generatedAt: "1970-01-01T00:00:00.000Z",
    source: "github-releases",
    releases: [normalized]
  });
  return normalized;
}

function normalizeWebsiteRelease(release) {
  const byName = Object.fromEntries((release.assets ?? []).map((asset) => [asset.name, asset.browser_download_url]));
  const title = release.body?.match(/^#.+?—\s*(.+)$/m)?.[1] ?? release.name ?? release.tag_name;
  return {
    tag: release.tag_name,
    name: release.name ?? release.tag_name,
    title,
    publishedAt: release.published_at,
    url: release.html_url,
    summary: String(release.body ?? "")
      .split("\n")
      .map((line) => line.trim())
      .filter((line) => /^[-*] /.test(line))
      .map((line) => line.replace(/^[-*] +/, "").replace(/[*_`]/g, ""))
      .slice(0, 5),
    assets: {
      installer: byName["DropSpaceSetup.exe"],
      portable: byName["DropSpace.exe"],
      msix: byName["DropSpace-x64.msix"],
      checksums: byName["SHA256SUMS.txt"]
    }
  };
}

function validateWebsiteEntry(entry, apiByTag, prerelease) {
  const apiRelease = apiByTag.get(entry?.tag);
  if (!apiRelease || apiRelease.isPrerelease !== prerelease || entry.url !== apiRelease.htmlUrl ||
      entry.name !== apiRelease.name || entry.publishedAt !== apiRelease.publishedAt ||
      typeof entry.title !== "string" || !Array.isArray(entry.summary)) {
    throw new TypeError("Website release entry does not match the validated release API.");
  }
  const apiAssets = Object.fromEntries(apiRelease.assets.map((asset) => [asset.name, asset.downloadUrl]));
  const expected = {
    installer: apiAssets["DropSpaceSetup.exe"],
    portable: apiAssets["DropSpace.exe"],
    msix: apiAssets["DropSpace-x64.msix"],
    checksums: apiAssets["SHA256SUMS.txt"]
  };
  for (const [kind, url] of Object.entries(expected)) {
    if (!url || entry.assets?.[kind] !== url) throw new TypeError(`Website release is missing its ${kind} asset.`);
  }
}

function requireReleaseAssets(release, label) {
  const names = new Set(release?.assets.map((asset) => asset.name));
  for (const name of REQUIRED_RELEASE_ASSETS) {
    if (!names?.has(name)) throw new TypeError(`${label} release is missing required asset ${name}.`);
  }
}

function isIsoDate(value) {
  return typeof value === "string" && Number.isFinite(Date.parse(value));
}

function normalizeOfficialWebsiteReferences(body) {
  return body
    .replaceAll("https://dropspace.pages.dev/", "https://airanluo-dot.github.io/DropSpace/")
    .replace(
      "The app tries `dropspace.pages.dev`, the GitHub Pages mirror, then GitHub REST.",
      "The app tries the official GitHub Pages endpoint, then GitHub REST.")
    .replace(
      "Cloudflare Pages serves a short-cached dynamic endpoint, while GitHub Pages keeps a build-time fallback.",
      "GitHub Pages serves the versioned contract generated from validated GitHub Release metadata.");
}
