export const RELEASE_API_SCHEMA_VERSION = 1;
export const RELEASE_API_MAX_ITEMS = 20;

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
  if (payload.releases.length > RELEASE_API_MAX_ITEMS) throw new RangeError("Too many releases.");
  for (const release of payload.releases) {
    if (!/^v\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(release.tagName ?? "")) throw new TypeError("Invalid release tag.");
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
    body: String(release.body ?? ""),
    isDraft: false,
    isPrerelease: Boolean(release.prerelease),
    publishedAt: release.published_at ?? null,
    htmlUrl,
    assets
  };
  validateReleaseApi({ schemaVersion: RELEASE_API_SCHEMA_VERSION, releases: [normalized] });
  return normalized;
}
