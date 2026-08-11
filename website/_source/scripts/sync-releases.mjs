import { readFile, writeFile } from "node:fs/promises";

const API = "https://api.github.com/repos/airanluo-dot/DropSpace/releases?per_page=20";
const OUTPUT = new URL("../data/releases.json", import.meta.url);

function assetMap(assets = []) {
  const byName = Object.fromEntries(assets.map((asset) => [asset.name, asset.browser_download_url]));
  return {
    installer: byName["DropSpaceSetup.exe"],
    portable: byName["DropSpace.exe"],
    msix: byName["DropSpace-x64.msix"],
    checksums: byName["SHA256SUMS.txt"]
  };
}

function summary(body = "") {
  return body
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => /^[-*] /.test(line))
    .map((line) => line.replace(/^[-*] +/, "").replace(/[*_`]/g, ""))
    .slice(0, 5);
}

function normalize(release) {
  const title = release.body?.match(/^#.+?—\s*(.+)$/m)?.[1] ?? release.name ?? release.tag_name;
  return {
    tag: release.tag_name,
    name: release.name ?? release.tag_name,
    title,
    publishedAt: release.published_at,
    url: release.html_url,
    summary: summary(release.body),
    assets: assetMap(release.assets)
  };
}

try {
  const response = await fetch(API, {
    headers: { Accept: "application/vnd.github+json", "User-Agent": "DropSpace-Website" },
    signal: AbortSignal.timeout(12000)
  });
  if (!response.ok) throw new Error(`GitHub API returned ${response.status}`);
  const releases = (await response.json()).filter((release) => !release.draft);
  const stable = releases.find((release) => !release.prerelease);
  if (!stable) throw new Error("No Stable release found");
  const data = {
    syncedAt: new Date().toISOString(),
    source: "github",
    stable: normalize(stable),
    previews: releases.filter((release) => release.prerelease).slice(0, 5).map(normalize)
  };
  await writeFile(OUTPUT, `${JSON.stringify(data, null, 2)}\n`);
  console.log(`Synced ${data.stable.tag} and ${data.previews.length} Preview releases.`);
} catch (error) {
  const fallback = JSON.parse(await readFile(OUTPUT, "utf8"));
  if (!fallback.stable?.assets?.installer) throw error;
  console.warn(`Release sync unavailable; using committed fallback (${error.message}).`);
}
