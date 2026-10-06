import { readFile } from "node:fs/promises";
import { createHash } from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { validateLatestChangeApi } from "./release-contract.mjs";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../..");
const tag = process.argv[2]?.trim();
const waitSeconds = Number(process.argv[3] ?? "0");
const verificationMode = process.argv[4] ?? "release";
if (!/^v\d+\.\d+\.\d+(?:-(?:preview|beta)\.\d+)?$/.test(tag ?? "")) throw new Error("Pass a valid release tag.");
if (!Number.isInteger(waitSeconds) || waitSeconds < 0 || waitSeconds > 1200) throw new Error("Wait seconds must be between 0 and 1200.");
if (!["release", "live"].includes(verificationMode)) throw new Error("Verification mode must be release or live.");

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
if (release.prerelease !== /-(?:preview|beta)\./.test(tag)) throw new Error(`${tag} has the wrong prerelease flag.`);
const assets = new Map(release.assets.map((asset) => [asset.name, asset]));
const cudaAsset = `DropSpace-CUDA-win-x64-${tag}.zip`;
const cudaMetadata = ["cuda-runtime-download.json", "cuda-runtime-manifest.json"];
const optionalAssets = new Set(["runtime-publication.json", cudaAsset, ...cudaMetadata]);
if (assets.size !== release.assets.length || expectedAssets.some((name) => !assets.has(name)) ||
    [...assets.keys()].some(name => !expectedAssets.includes(name) && !optionalAssets.has(name)) ||
    (assets.has(cudaAsset) && !assets.has("runtime-publication.json")) ||
    (cudaMetadata.some(name => assets.has(name)) &&
      (!assets.has(cudaAsset) || cudaMetadata.some(name => !assets.has(name))))) {
  throw new Error(`${tag} does not expose the exact public asset contract.`);
}
for (const [name, asset] of assets) {
  if (!(asset.size > 0)) throw new Error(`${name} is empty.`);
  const expectedPrefix = `https://github.com/${repository}/releases/download/${tag}/`;
  if (!asset.browser_download_url.startsWith(expectedPrefix)) throw new Error(`${name} has an unofficial download URL.`);
}

let expectedSummary;
if (verificationMode === "release") {
  const notes = await readFile(path.join(root, `.github/release-notes/${tag}.md`), "utf8");
  const summaryMatches = [...notes.matchAll(/^\s*<!--\s*update-summary:\s*([^\r\n]*?)\s*-->\s*$/gim)];
  if (summaryMatches.length !== 1) throw new Error(`${tag} release notes do not have exactly one update summary.`);
  expectedSummary = summaryMatches[0][1].trim();
}
const manifestText = await (await fetchOk(assets.get("update-manifest.json").browser_download_url)).text();
const manifest = JSON.parse(manifestText);
const semanticVersion = tag.slice(1);
if (manifest.version !== semanticVersion || manifest.channel !== (tag.includes("-preview.") ? "preview" : release.prerelease ? "beta" : "stable")) {
  throw new Error("Published manifest version/channel does not match the GitHub Release.");
}
if (verificationMode === "release" && manifest.summary !== expectedSummary) throw new Error("Published manifest summary does not match release notes.");
if (verificationMode === "live" && (typeof manifest.summary !== "string" || !manifest.summary.trim())) throw new Error("Published manifest summary is empty.");
if (manifest.installer?.assetName !== "DropSpaceSetup.exe" || manifest.portable?.assetName !== "DropSpace.exe") {
  throw new Error("Published manifest uses unexpected executable asset names.");
}
const checksums = await (await fetchOk(assets.get("SHA256SUMS.txt").browser_download_url)).text();
const expectedChecksumAssets = new Set([...assets.keys()].filter(name => name !== "SHA256SUMS.txt" && name !== "update-manifest.json"));
const checksumMap = new Map();
for (const line of checksums.trim().split(/\r?\n/)) {
  const match = line.match(/^([0-9a-f]{64})\s{2}(.+)$/i);
  if (!match) throw new Error("SHA256SUMS.txt contains an invalid line.");
  if ((!expectedChecksumAssets.has(match[2]) && match[2] !== "update-manifest.json") || checksumMap.has(match[2])) {
    throw new Error("SHA256SUMS.txt contains an unexpected or duplicate download.");
  }
  checksumMap.set(match[2], match[1].toLowerCase());
}
if ([...expectedChecksumAssets].some(name => !checksumMap.has(name)) ||
    checksumMap.get("DropSpaceSetup.exe") !== manifest.installer.sha256 || checksumMap.get("DropSpace.exe") !== manifest.portable.sha256) {
  throw new Error("Published checksums and update manifest disagree.");
}
for (const [name, digest] of checksumMap) {
  const apiDigest = assets.get(name)?.digest;
  if (apiDigest && apiDigest !== `sha256:${digest}`) throw new Error(`Published checksum and uploaded asset digest disagree: ${name}.`);
}
if (checksumMap.has("update-manifest.json") &&
    checksumMap.get("update-manifest.json") !== createHash("sha256").update(manifestText).digest("hex")) {
  throw new Error("Downloaded update manifest does not match its published checksum.");
}

if (assets.has(cudaMetadata[0])) {
  const metadata = new Map();
  for (const name of cudaMetadata) {
    const bytes = Buffer.from(await (await fetchOk(assets.get(name).browser_download_url)).arrayBuffer());
    if (bytes.length !== assets.get(name).size ||
        createHash("sha256").update(bytes).digest("hex") !== checksumMap.get(name)) {
      throw new Error(`Downloaded CUDA metadata does not match its published identity: ${name}.`);
    }
    metadata.set(name, bytes);
  }
  const descriptor = JSON.parse(metadata.get(cudaMetadata[0]).toString("utf8"));
  if (descriptor.appRelease?.tag !== tag || descriptor.appRelease?.sourceCommit !== release.target_commitish ||
      descriptor.download?.name !== cudaAsset || descriptor.download?.bytes !== assets.get(cudaAsset).size ||
      descriptor.download?.url !== assets.get(cudaAsset).browser_download_url ||
      descriptor.download?.sha256 !== checksumMap.get(cudaAsset) ||
      descriptor.manifest?.name !== cudaMetadata[1] ||
      descriptor.manifest?.bytes !== metadata.get(cudaMetadata[1]).length ||
      descriptor.manifest?.sha256 !== checksumMap.get(cudaMetadata[1])) {
    throw new Error("Published CUDA metadata does not bind the release and runtime assets.");
  }
}

let api;
let latestChangeApi;
do {
  try {
    api = await (await fetchOk(`${siteOrigin}/api/v1/releases.json?verify=${Date.now()}`)).json();
    const item = api.releases?.find((candidate) => candidate.tagName === tag);
    if (!item) throw new Error(`${tag} is not in the official website API yet.`);
    if (item.isPrerelease !== release.prerelease || item.htmlUrl !== release.html_url) throw new Error("Website API release identity disagrees with GitHub.");
    const siteAssets = new Map(item.assets.map((asset) => [asset.name, asset]));
    for (const name of assets.keys()) {
      if (siteAssets.get(name)?.downloadUrl !== assets.get(name).browser_download_url) throw new Error(`Website API ${name} URL disagrees with GitHub.`);
    }
    latestChangeApi = validateLatestChangeApi(await (await fetchOk(`${siteOrigin}/api/v1/latest-change.json?verify=${Date.now()}`)).json());
    if (verificationMode === "release" &&
        (latestChangeApi.release.tagName !== tag || latestChangeApi.release.htmlUrl !== release.html_url ||
         latestChangeApi.release.channel !== (release.prerelease ? "beta" : "stable"))) {
      throw new Error("Latest-change API does not present the newly published release.");
    }

    if (verificationMode === "live") {
      const latestSiteRelease = api.releases?.find(candidate => candidate.tagName === latestChangeApi.release.tagName);
      if (!latestSiteRelease || latestSiteRelease.htmlUrl !== latestChangeApi.release.htmlUrl ||
          latestSiteRelease.isPrerelease !== (latestChangeApi.release.channel === "beta")) {
        throw new Error("Latest-change API does not match the synchronized website release list.");
      }
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

const latestHome = await (await fetchOk(`${siteOrigin}/en/?verify=${Date.now()}`)).text();
const expectedLatestChangeTag = latestChangeApi.release.tagName;
if (!latestHome.includes(`data-latest-change-tag="">${expectedLatestChangeTag}<`)) {
  throw new Error(`The live website does not present ${expectedLatestChangeTag} in the latest-change design.`);
}

if (!release.prerelease) {
  if (!latestHome.includes(`Latest Stable · ${tag}`) || !latestHome.includes(`/releases/download/${tag}/DropSpaceSetup.exe`)) {
    throw new Error(`The live website does not present ${tag} as the latest Stable release.`);
  }
}

console.log(`Verified ${tag}: GitHub Release, ${assets.size} public assets, manifest, checksums, release API, latest-change API, and live website${release.prerelease ? "" : " Stable state"}.`);
