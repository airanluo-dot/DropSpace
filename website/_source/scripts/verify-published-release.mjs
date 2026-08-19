import { readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const tag = process.argv[2]?.trim();
const waitSeconds = Number(process.argv[3] ?? "0");
if (!/^v\d+\.\d+\.\d+(?:-preview\.\d+)?$/.test(tag ?? "")) throw new Error("Pass a valid release tag.");
if (!Number.isInteger(waitSeconds) || waitSeconds < 0 || waitSeconds > 1200) throw new Error("Wait seconds must be between 0 and 1200.");

const repository = "airanluo-dot/DropSpace";
const siteOrigin = "https://airanluo-dot.github.io/DropSpace";
const expectedAssets = ["DropSpace-x64.msix", "DropSpace.exe", "DropSpaceSetup.exe", "SHA256SUMS.txt", "update-manifest.json"];
const token = process.env.GITHUB_TOKEN ?? process.env.GH_TOKEN;
const headers = { Accept: "application/vnd.github+json", "User-Agent": "DropSpace-release-verifier" };
if (token) headers.Authorization = `Bearer ${token}`;

async function fetchOk(url, options = {}) {
  const response = await fetch(url, { signal: AbortSignal.timeout(20_000), ...options });
  if (!response.ok) throw new Error(`${url} returned HTTP ${response.status}.`);
  return response;
}

const deadline = Date.now() + waitSeconds * 1000;
let release;
let lastError;
do {
  try {
    release = await (await fetchOk(`https://api.github.com/repos/${repository}/releases/tags/${tag}`, { headers })).json();
    lastError = undefined;
    break;
  } catch (error) {
    lastError = error;
    if (Date.now() >= deadline) break;
    await new Promise((resolve) => setTimeout(resolve, 15_000));
  }
} while (Date.now() <= deadline);
if (lastError) throw lastError;
if (release.draft) throw new Error(`${tag} is still a draft.`);
if (release.prerelease !== tag.includes("-preview.")) throw new Error(`${tag} has the wrong prerelease flag.`);
const assets = new Map(release.assets.map((asset) => [asset.name, asset]));
if (assets.size !== expectedAssets.length || expectedAssets.some((name) => !assets.has(name))) {
  throw new Error(`${tag} does not expose the exact public asset contract.`);
}
for (const [name, asset] of assets) {
  if (!(asset.size > 0)) throw new Error(`${name} is empty.`);
  const expectedPrefix = `https://github.com/${repository}/releases/download/${tag}/`;
  if (!asset.browser_download_url.startsWith(expectedPrefix)) throw new Error(`${name} has an unofficial download URL.`);
}

const notes = await readFile(path.join(root, `.github/release-notes/${tag}.md`), "utf8");
const summaryMatches = [...notes.matchAll(/^\s*<!--\s*update-summary:\s*([^\r\n]*?)\s*-->\s*$/gim)];
if (summaryMatches.length !== 1) throw new Error(`${tag} release notes do not have exactly one update summary.`);
const expectedSummary = summaryMatches[0][1].trim();
const manifest = await (await fetchOk(assets.get("update-manifest.json").browser_download_url)).json();
const semanticVersion = tag.slice(1);
if (manifest.version !== semanticVersion || manifest.channel !== (release.prerelease ? "preview" : "stable")) {
  throw new Error("Published manifest version/channel does not match the GitHub Release.");
}
if (manifest.summary !== expectedSummary) throw new Error("Published manifest summary does not match release notes.");
if (manifest.installer?.assetName !== "DropSpaceSetup.exe" || manifest.portable?.assetName !== "DropSpace.exe") {
  throw new Error("Published manifest uses unexpected executable asset names.");
}
const checksums = await (await fetchOk(assets.get("SHA256SUMS.txt").browser_download_url)).text();
const checksumMap = new Map(checksums.trim().split(/\r?\n/).map((line) => {
  const match = line.match(/^([0-9a-f]{64})\s{2}(.+)$/i);
  if (!match) throw new Error("SHA256SUMS.txt contains an invalid line.");
  return [match[2], match[1].toLowerCase()];
}));
if (checksumMap.size !== 3 || checksumMap.get("DropSpaceSetup.exe") !== manifest.installer.sha256 || checksumMap.get("DropSpace.exe") !== manifest.portable.sha256) {
  throw new Error("Published checksums and update manifest disagree.");
}

let api;
do {
  try {
    api = await (await fetchOk(`${siteOrigin}/api/v1/releases.json?verify=${Date.now()}`)).json();
    const item = api.releases?.find((candidate) => candidate.tagName === tag);
    if (!item) throw new Error(`${tag} is not in the official website API yet.`);
    if (item.isPrerelease !== release.prerelease || item.htmlUrl !== release.html_url) throw new Error("Website API release identity disagrees with GitHub.");
    const siteAssets = new Map(item.assets.map((asset) => [asset.name, asset]));
    for (const name of expectedAssets) {
      if (siteAssets.get(name)?.downloadUrl !== assets.get(name).browser_download_url) throw new Error(`Website API ${name} URL disagrees with GitHub.`);
    }
    lastError = undefined;
    break;
  } catch (error) {
    lastError = error;
    if (Date.now() >= deadline) break;
    await new Promise((resolve) => setTimeout(resolve, 15_000));
  }
} while (Date.now() <= deadline);
if (lastError) throw lastError;

if (!release.prerelease) {
  const home = await (await fetchOk(`${siteOrigin}/en/?verify=${Date.now()}`)).text();
  if (!home.includes(`Latest Stable · ${tag}`) || !home.includes(`/releases/download/${tag}/DropSpaceSetup.exe`)) {
    throw new Error(`The live website does not present ${tag} as the latest Stable release.`);
  }
}

console.log(`Verified ${tag}: GitHub Release, five public assets, manifest, checksums, website API${release.prerelease ? "" : ", and Stable website"}.`);
