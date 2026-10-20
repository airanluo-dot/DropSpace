import { createHash } from "node:crypto";
import { validateMacManifest, validateMacReleaseData, MAC_TAG } from "./macos-release-contract.mjs";

const base = "https://airanluo-dot.github.io/DropSpace/";
async function fetchBytes(url, limit) {
  let response;
  for (let count = 0; count < 6; count++) {
    const parsed = new URL(url);
    if (parsed.protocol !== "https:" || parsed.username || parsed.password || parsed.port ||
        !["airanluo-dot.github.io", "github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"].includes(parsed.hostname)) {
      throw Error("Untrusted public verification URL");
    }
    response = await fetch(url, { redirect: "manual", signal: AbortSignal.timeout(120000) });
    if (![301, 302, 303, 307, 308].includes(response.status)) break;
    await response.body?.cancel();
    if (!response.headers.get("location")) throw Error("Download redirect missing location");
    url = new URL(response.headers.get("location"), url).href;
  }
  if (!response?.ok) throw Error(`Public download returned HTTP ${response?.status}`);
  let size = 0; const hash = createHash("sha256"); const chunks = [];
  for await (const chunk of response.body) {
    size += chunk.length;
    if (size > limit) throw Error("Public artifact exceeds declared size");
    hash.update(chunk);
    if (limit <= 4 * 1024 ** 2) chunks.push(chunk);
  }
  return { size, sha256: hash.digest("hex"), text: chunks.length ? Buffer.concat(chunks).toString("utf8") : null };
}

export async function verifyPublishedMac(tag) {
  if (!MAC_TAG.test(tag ?? "")) throw Error("An exact macos-v… release tag is required");
  const data = validateMacReleaseData(JSON.parse((await fetchBytes(base + "api/macos/v1/releases.json", 4 * 1024 ** 2)).text));
  const entry = data.releases.find(item => item.manifest.tag === tag);
  if (!entry) throw Error("Published Mac release is absent from the Mac API");
  const manifest = validateMacManifest(JSON.parse((await fetchBytes(`https://github.com/airanluo-dot/DropSpace/releases/download/${tag}/update-manifest-macos.json`, 131072)).text));
  if (JSON.stringify(entry.manifest) !== JSON.stringify(manifest)) throw Error("Website and release manifests differ");
  const windows = JSON.parse((await fetchBytes(base + "api/v1/releases.json", 4 * 1024 ** 2)).text);
  if (windows.releases.some(item => item.tagName?.startsWith("macos-"))) throw Error("Mac release leaked into the Windows update feed");
  const page = (await fetchBytes(base + "macos/", 4 * 1024 ** 2)).text;
  if (!page.includes(manifest.installer.url) || !page.includes("No Developer ID signature")) throw Error("Mac page lacks the download or required signature disclosure");
  for (const channel of ["stable", "beta"]) {
    const feed = JSON.parse((await fetchBytes(base + `api/macos/v1/${channel}.json`, 131072)).text);
    if (feed.platform !== "macos" || feed.channel !== channel ||
        JSON.stringify(feed.release) !== JSON.stringify(data[channel]?.manifest ?? null)) throw Error("Mac channel feed mismatch");
  }
  const downloaded = [];
  const checksumText = (await fetchBytes(entry.checksumsURL, 16384)).text;
  const checksums = new Map(checksumText.trim().split(/\r?\n/).map(line => {
    const match = /^([0-9a-f]{64}) {2}([A-Za-z0-9.-]+)$/.exec(line);
    if (!match) throw Error("Invalid published checksum line");
    return [match[2], match[1]];
  }));
  for (const asset of [manifest.archive, manifest.installer]) {
    const bytes = await fetchBytes(asset.url, asset.size);
    if (bytes.size !== asset.size || bytes.sha256 !== asset.sha256 || checksums.get(asset.name) !== bytes.sha256) throw Error(`Downloaded bytes disagree for ${asset.name}`);
    downloaded.push({ name: asset.name, size: bytes.size, sha256: bytes.sha256 });
  }
  return { tag, website: base + "macos/", downloaded, signing: "ad-hoc", notarized: false,
    scope: "Actual public HTTP downloads and platform isolation. Does not establish installation, launch, Gatekeeper or update acceptance." };
}

if (process.argv[1] && new URL(import.meta.url).pathname === process.argv[1]) {
  console.log(JSON.stringify(await verifyPublishedMac(process.argv[2]), null, 2));
}
