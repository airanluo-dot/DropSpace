import { normalizeGitHubReleases } from "../../../scripts/release-contract.mjs";

const RELEASES_API = "https://api.github.com/repos/airanluo-dot/DropSpace/releases?per_page=20&page=1";
const CACHE_SECONDS = 90;

export async function onRequestGet(context) {
  const cache = caches.default;
  const cacheKey = new Request(new URL("/__dropspace-api/v1/releases", context.request.url), { method: "GET" });
  const cached = await cache.match(cacheKey);
  if (cached) return cached;

  const upstream = await fetch(RELEASES_API, {
    headers: {
      Accept: "application/vnd.github+json",
      "User-Agent": "DropSpace-Website-Release-API",
      "X-GitHub-Api-Version": "2022-11-28"
    }
  });
  if (!upstream.ok) {
    return Response.json({ error: "release_metadata_unavailable" }, {
      status: 503,
      headers: { "Cache-Control": "no-store" }
    });
  }

  const payload = normalizeGitHubReleases(await upstream.json());
  const response = Response.json(payload, {
    headers: {
      "Cache-Control": `public, max-age=${CACHE_SECONDS}, s-maxage=${CACHE_SECONDS}, stale-while-revalidate=300`,
      "Access-Control-Allow-Origin": "*",
      "X-Content-Type-Options": "nosniff"
    }
  });
  context.waitUntil(cache.put(cacheKey, response.clone()));
  return response;
}

