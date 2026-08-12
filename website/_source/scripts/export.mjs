import { cp, readdir, rm } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const dist = path.join(root, "dist");
const target = path.dirname(root);
for (const entry of await readdir(target)) {
  if (entry === "_source") continue;
  await rm(path.join(target, entry), { recursive: true, force: true });
}
for (const entry of await readdir(dist)) {
  await cp(path.join(dist, entry), path.join(target, entry), { recursive: true });
}
console.log("Exported generated site to website/.");
