import { compareReleaseTags } from "./release-contract.mjs";

export const MAC_TAG = /^macos-v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-beta\.([1-9]\d*))?$/;
export const MAC_ASSETS = Object.freeze({
  archive: "DropSpace-macOS-arm64.zip", installer: "DropSpace-macOS-arm64.dmg",
  checksums: "SHA256SUMS-macos.txt", manifest: "update-manifest-macos.json"
});
const prefix = "https://github.com/airanluo-dot/DropSpace/releases/";
function selectedMacReleases(github) {
  const published = github.filter(release => !release.draft && MAC_TAG.test(release.tag_name ?? ""))
    .sort((a, b) => compareReleaseTags(b.tag_name.slice(6), a.tag_name.slice(6)));
  const recent = published.slice(0, 20);
  const stable = published.find(release => !release.prerelease);
  return stable && !recent.some(release => release.tag_name === stable.tag_name)
    ? [...recent.slice(0, 19), stable] : recent;
}

export function validateMacManifest(manifest, release) {
  const tag = manifest?.tag;
  if (!MAC_TAG.test(tag ?? "") || manifest.schemaVersion !== 1 || manifest.platform !== "macos" ||
      manifest.architecture !== "arm64" || manifest.bundleID !== "io.github.airanluo-dot.DropSpace.macOS" ||
      !/^[1-9]\d*$/.test(manifest.bundleVersion ?? "") ||
      !/^\d+\.\d+(?:\.\d+)?$/.test(manifest.minimumOS ?? "") || Number(manifest.minimumOS.split(".")[0]) < 26 ||
      manifest.windowsBaseline !== "v0.3.2-beta.7" || manifest.signing !== "ad-hoc" || manifest.notarized !== false ||
      manifest.channel !== (tag.includes("-beta.") ? "beta" : "stable") ||
      typeof manifest.releaseNotes !== "string" || Buffer.byteLength(manifest.releaseNotes) > 65536 ||
      manifest.releaseURL !== `${prefix}tag/${tag}`) throw new TypeError("Invalid macOS update manifest.");
  for (const kind of ["archive", "installer"]) {
    const asset = manifest[kind];
    if (!asset || asset.name !== MAC_ASSETS[kind] || asset.url !== `${prefix}download/${tag}/${asset.name}` ||
        !Number.isSafeInteger(asset.size) || asset.size <= 0 || asset.size > 2 * 1024 ** 3 ||
        !/^[0-9a-f]{64}$/.test(asset.sha256 ?? "")) throw new TypeError("Invalid macOS release asset.");
    if (release) {
      const published = release.assets?.find(item => item.name === asset.name);
      if (!published || published.size !== asset.size || published.browser_download_url !== asset.url ||
          (published.digest && published.digest !== `sha256:${asset.sha256}`)) throw new TypeError("macOS manifest disagrees with published asset bytes.");
    }
  }
  if (release && (release.tag_name !== tag || Boolean(release.prerelease) !== (manifest.channel === "beta"))) {
    throw new TypeError("macOS release channel or tag mismatch.");
  }
  return manifest;
}

export function createMacReleaseData(github, manifests = new Map(), generatedAt = new Date().toISOString()) {
  if (!Array.isArray(github)) throw new TypeError("Expected GitHub release array.");
  const entries = selectedMacReleases(github).map(release => {
    if (release.html_url !== `${prefix}tag/${release.tag_name}` || !Number.isFinite(Date.parse(release.published_at))) {
      throw new TypeError("Invalid published macOS release.");
    }
    for (const name of Object.values(MAC_ASSETS)) {
      const matches = release.assets?.filter(asset => asset.name === name) ?? [];
      if (matches.length !== 1 || !Number.isSafeInteger(matches[0].size) || matches[0].size <= 0 ||
          matches[0].browser_download_url !== `${prefix}download/${release.tag_name}/${name}`) {
        throw new TypeError(`Missing or invalid macOS artifact: ${name}`);
      }
    }
    const manifest = validateMacManifest(manifests.get(release.tag_name), release);
    return { publishedAt: release.published_at, manifest, checksumsURL: `${prefix}download/${manifest.tag}/${MAC_ASSETS.checksums}` };
  });
  const stable = entries.find(entry => entry.manifest.channel === "stable") ?? null;
  return validateMacReleaseData({ schemaVersion: 1, platform: "macos", generatedAt, releases: entries, stable, beta: entries[0] ?? null });
}

export function validateMacReleaseData(data) {
  if (!data || data.schemaVersion !== 1 || data.platform !== "macos" || !Number.isFinite(Date.parse(data.generatedAt)) ||
      !Array.isArray(data.releases) || data.releases.length > 20) throw new TypeError("Invalid macOS release feed.");
  const tags = new Set();
  for (const entry of data.releases) {
    validateMacManifest(entry.manifest);
    if (!Number.isFinite(Date.parse(entry.publishedAt)) || tags.has(entry.manifest.tag) ||
        entry.checksumsURL !== `${prefix}download/${entry.manifest.tag}/${MAC_ASSETS.checksums}`) throw new TypeError("Invalid macOS release entry.");
    tags.add(entry.manifest.tag);
  }
  const sorted = [...data.releases].sort((a, b) => compareReleaseTags(b.manifest.tag.slice(6), a.manifest.tag.slice(6)));
  if (JSON.stringify(data.releases) !== JSON.stringify(sorted) ||
      JSON.stringify(data.stable) !== JSON.stringify(sorted.find(entry => entry.manifest.channel === "stable") ?? null) ||
      JSON.stringify(data.beta) !== JSON.stringify(sorted[0] ?? null)) throw new TypeError("macOS channel selection mismatch.");
  return data;
}

export function macChannelFeed(data, channel) {
  validateMacReleaseData(data);
  if (!["stable", "beta"].includes(channel)) throw new TypeError("Invalid macOS channel.");
  return { schemaVersion: 1, platform: "macos", channel, generatedAt: data.generatedAt, release: data[channel]?.manifest ?? null };
}

export async function fetchMacManifests(releases, fetchImpl = fetch) {
  const result = new Map();
  const candidates = selectedMacReleases(releases);
  for (const release of candidates) {
    let url = `${prefix}download/${release.tag_name}/${MAC_ASSETS.manifest}`;
    let response;
    for (let i = 0; i < 6; i++) {
      const parsed = new URL(url);
      if (parsed.protocol !== "https:" || parsed.username || parsed.password || parsed.port ||
          !["github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"].includes(parsed.hostname)) {
        throw new TypeError("Untrusted macOS manifest redirect.");
      }
      response = await fetchImpl(url, { redirect: "manual", signal: AbortSignal.timeout(12000) });
      if (![301, 302, 303, 307, 308].includes(response.status)) break;
      await response.body?.cancel();
      const next = response.headers.get("location");
      if (!next) throw new Error("Missing manifest redirect.");
      url = new URL(next, url).href;
    }
    if (!response?.ok) throw new Error(`macOS manifest download failed: ${response?.status}`);
    let size = 0; const chunks = [];
    for await (const chunk of response.body) {
      size += chunk.length;
      if (size > 131072) throw new RangeError("macOS manifest too large.");
      chunks.push(chunk);
    }
    result.set(release.tag_name, JSON.parse(Buffer.concat(chunks).toString("utf8")));
  }
  return result;
}
