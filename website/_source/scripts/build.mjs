import { cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const src = path.join(root, "src");
const dist = path.join(root, "dist");
const releases = JSON.parse(await readFile(path.join(root, "data/releases.json"), "utf8"));

const stable = releases.stable;
const replacements = {
  "{{STABLE_TAG}}": stable.tag,
  "{{STABLE_TITLE}}": stable.title,
  "{{STABLE_DATE}}": new Intl.DateTimeFormat("en-US", { dateStyle: "long", timeZone: "UTC" }).format(new Date(stable.publishedAt)),
  "{{STABLE_URL}}": stable.url,
  "{{INSTALLER_URL}}": stable.assets.installer,
  "{{PORTABLE_URL}}": stable.assets.portable,
  "{{MSIX_URL}}": stable.assets.msix,
  "{{CHECKSUM_URL}}": stable.assets.checksums
};

await rm(dist, { recursive: true, force: true });
await mkdir(dist, { recursive: true });
await cp(src, dist, { recursive: true });

for (const relative of ["index.html", "changelog/index.html", "404.html", "site.webmanifest", "sitemap.xml"]) {
  const file = path.join(dist, relative);
  let contents = await readFile(file, "utf8");
  for (const [token, value] of Object.entries(replacements)) contents = contents.split(token).join(value ?? stable.url);
  await writeFile(file, contents);
}

await writeFile(path.join(dist, "release-data.json"), `${JSON.stringify(releases, null, 2)}\n`);
console.log(`Built DropSpace website for ${stable.tag}.`);
