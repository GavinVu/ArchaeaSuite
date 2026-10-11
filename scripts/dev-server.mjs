// Serves a local copy of the catalog and the packages in ./packages so the
// whole install pipeline can be tested without publishing anything.
//
//   node scripts/dev-server.mjs            (terminal 1)
//   $env:ARCHAEA_DEV_CATALOG = "http://127.0.0.1:8765/catalog.json"
//   npm run tauri dev                      (terminal 2)
//
// Package URLs in the served catalog are rewritten to point at this server.
// Only debug builds honour ARCHAEA_DEV_CATALOG; release builds ignore it.
// Set HELLO_VERSION / FAIL=checksum to simulate an update or a bad download.

import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import { dirname, extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const port = Number(process.env.PORT ?? 8765);
const base = `http://127.0.0.1:${port}`;
const types = { ".json": "application/json", ".png": "image/png", ".zip": "application/zip" };

async function devCatalog() {
  const catalog = JSON.parse(await readFile(join(root, "catalog", "catalog.json"), "utf8"));
  for (const app of catalog.apps) {
    for (const [arch, pkg] of Object.entries(app.packages ?? {})) {
      const version = (app.id === "archaea-hello" && process.env.HELLO_VERSION) || app.version;
      const file = `${app.id}-${version}-${arch}.zip`;
      const bytes = await readFile(join(root, "packages", file));
      app.version = version;
      pkg.url = `${base}/packages/${file}`;
      pkg.size = bytes.length;
      pkg.sha256 =
        process.env.FAIL === "checksum"
          ? "0".repeat(64)
          : createHash("sha256").update(bytes).digest("hex");
    }
  }
  return JSON.stringify(catalog);
}

createServer(async (req, res) => {
  try {
    const path = decodeURIComponent(new URL(req.url, base).pathname);
    if (path === "/catalog.json") {
      res.writeHead(200, { "content-type": types[".json"] });
      return res.end(await devCatalog());
    }
    const dir = path.startsWith("/packages/") ? root : join(root, "catalog");
    const file = normalize(join(dir, path));
    if (!file.startsWith(dir)) throw new Error("outside root");
    const info = await stat(file);
    res.writeHead(200, {
      "content-type": types[extname(file)] ?? "application/octet-stream",
      "content-length": info.size,
    });
    res.end(await readFile(file));
  } catch {
    res.writeHead(404).end("not found");
  }
}).listen(port, "127.0.0.1", () => console.log(`dev catalog: ${base}/catalog.json`));
