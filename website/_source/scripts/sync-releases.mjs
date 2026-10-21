import { readFile, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createWebsiteReleaseData, validateWebsiteReleaseData, OFFICIAL_RELEASES_API } from "./release-contract.mjs";
import { createMacReleaseData, fetchMacManifests } from "./macos-release-contract.mjs";
const DEFAULT_OUTPUT = fileURLToPath(new URL("../data/releases.json", import.meta.url));

export async function syncAuthoritativeReleases({
  apiUrl = OFFICIAL_RELEASES_API,
  output = DEFAULT_OUTPUT,
  fetchImpl = fetch,
  token = process.env.GITHUB_TOKEN
} = {}) {
  const headers = {
    Accept: "application/vnd.github+json",
    "X-GitHub-Api-Version": "2022-11-28",
    "User-Agent": "DropSpace-Website"
  };
  if (token) headers.Authorization = `Bearer ${token}`;

  const response = await fetchImpl(apiUrl, {
    headers,
    signal: AbortSignal.timeout(12000)
  });
  if (!response.ok) throw new Error(`GitHub Releases API returned HTTP ${response.status}.`);

  const releases = await response.json();
  if (!Array.isArray(releases)) throw new TypeError("GitHub release payload must be an array.");
  // Page through the authoritative list before platform selection. A busy Mac or
  // component channel must not push the current Windows Stable out of the window.
  let link = response.headers?.get?.("link");
  for (let page = 2; /rel="next"/.test(link ?? ""); page++) {
    if (page > 20) throw new RangeError("Release pagination exceeded the bounded window.");
    const nextURL = new URL(apiUrl); nextURL.searchParams.set("page", String(page));
    const next = await fetchImpl(nextURL.href, { headers, signal: AbortSignal.timeout(12000) });
    if (!next.ok) throw new Error(`GitHub Releases page returned HTTP ${next.status}.`);
    const entries = await next.json();
    if (!Array.isArray(entries)) throw new TypeError("Invalid GitHub release page.");
    releases.push(...entries); link = next.headers?.get?.("link");
  }
  const data = createWebsiteReleaseData(releases);
  data.macos = createMacReleaseData(releases, await fetchMacManifests(releases, fetchImpl), data.syncedAt);
  await writeFile(output, `${JSON.stringify(data, null, 2)}\n`);
  return data;
}

export async function loadExplicitFixture(fixture, { output = DEFAULT_OUTPUT, validateOnly = false } = {}) {
  if (process.env.NODE_ENV === "production") {
    throw new Error("Fixture mode is forbidden in production.");
  }
  const data = validateWebsiteReleaseData(JSON.parse(await readFile(fixture, "utf8")));
  if (!validateOnly) await writeFile(output, `${JSON.stringify(data, null, 2)}\n`);
  return data;
}

async function main() {
  const args = process.argv.slice(2);
  const valueAfter = (flag) => {
    const index = args.indexOf(flag);
    return index >= 0 ? args[index + 1] : undefined;
  };
  const fixture = valueAfter("--fixture");
  const validateOnly = args.includes("--validate-only");
  const testOutput = process.env.NODE_ENV === "test" ? process.env.DROPSPACE_TEST_RELEASES_OUTPUT : undefined;
  const output = path.resolve(valueAfter("--output") ?? testOutput ?? DEFAULT_OUTPUT);

  let data;
  if (fixture) {
    data = await loadExplicitFixture(path.resolve(fixture), { output, validateOnly });
    console.log(`Validated explicit release fixture (${data.stable.tag}).`);
    return;
  }
  if (validateOnly) throw new Error("--validate-only requires --fixture.");

  const testApi = process.env.NODE_ENV === "test" ? process.env.DROPSPACE_TEST_RELEASES_API : undefined;
  data = await syncAuthoritativeReleases({ apiUrl: testApi ?? OFFICIAL_RELEASES_API, output });
  console.log(`Synced ${data.stable.tag} and ${data.prereleases.length} prereleases from GitHub Releases.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  await main();
}
