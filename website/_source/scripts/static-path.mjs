import path from "node:path";

export function resolveStaticPath(root, pathname) {
  const relative = decodeURIComponent(pathname).replace(/^\/DropSpace(?:\/|$)/, "/");
  const file = path.resolve(root, "." + relative);
  const contained = path.relative(root, file);
  if (contained === ".." || contained.startsWith(".." + path.sep) || path.isAbsolute(contained)) {
    throw new Error("outside root");
  }
  return file;
}
