import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { resolveStaticPath } from "./static-path.mjs";

const root = path.join(path.dirname(path.dirname(fileURLToPath(import.meta.url))), "dist");
const types = { ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8", ".json": "application/json", ".png": "image/png", ".webp": "image/webp", ".webm": "video/webm", ".xml": "application/xml", ".txt": "text/plain; charset=utf-8" };

createServer(async (request, response) => {
  try {
    const url = new URL(request.url, "http://127.0.0.1");
    let file = resolveStaticPath(root, url.pathname);
    if ((await stat(file)).isDirectory()) file = path.join(file, "index.html");
    const content = await readFile(file);
    response.writeHead(200, { "Content-Type": types[path.extname(file)] ?? "application/octet-stream", "Cache-Control": "no-store" });
    response.end(content);
  } catch {
    response.writeHead(404, { "Content-Type": "text/html; charset=utf-8" });
    response.end(await readFile(path.join(root, "404.html")));
  }
}).listen(4173, "127.0.0.1", () => console.log("DropSpace website available on 4173"));
