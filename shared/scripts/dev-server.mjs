// Serves a local copy of the catalog and the packages in ./packages so the
// whole install pipeline can be tested without publishing anything.
//
//   node shared/scripts/dev-server.mjs     (terminal 1)
//   $env:ARCHAEA_DEV_CATALOG = "http://127.0.0.1:8765/catalog.json"
//   cd launcher; npm run tauri dev         (terminal 2)
//
// Package URLs in the served catalog are rewritten to point at this server.
// An app whose package ZIP is not in ./packages is served as "coming-soon".
// Only debug builds honour ARCHAEA_DEV_CATALOG; release builds ignore it.
// Environment switches:
//   HELLO_VERSION=1.1.0   serve that version of the test app (simulate an update)
//   PHOTO_VERSION=0.1.1   serve that version of ArchaeaPhoto
//   IDE_VERSION=0.1.1     serve that version of ArchaeaIDE
//   FAIL=checksum         advertise a wrong SHA-256 (simulate a corrupt download)
//   THROTTLE_KBPS=2000    slow package downloads down to watch progress / cancel

import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import { dirname, extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const port = Number(process.env.PORT ?? 8765);
const base = `http://127.0.0.1:${port}`;
const types = { ".json": "application/json", ".png": "image/png", ".zip": "application/zip" };
const versionOverride = {
  "archaea-hello": process.env.HELLO_VERSION,
  "archaea-photo": process.env.PHOTO_VERSION,
  "archaea-ide": process.env.IDE_VERSION,
};

async function devCatalog() {
  const catalog = JSON.parse(await readFile(join(root, "catalog", "catalog.json"), "utf8"));
  for (const app of catalog.apps) {
    const version = versionOverride[app.id] || app.version;
    for (const [arch, pkg] of Object.entries(app.packages ?? {})) {
      const file = `${app.id}-${version}-${arch}.zip`;
      let bytes;
      try {
        bytes = await readFile(join(root, "packages", file));
      } catch {
        console.warn(`missing packages/${file}; not offering ${app.id} for ${arch}`);
        delete app.packages[arch];
        continue;
      }
      pkg.url = `${base}/packages/${file}`;
      pkg.size = bytes.length;
      pkg.sha256 =
        process.env.FAIL === "checksum"
          ? "0".repeat(64)
          : createHash("sha256").update(bytes).digest("hex");
    }
    if (app.packages && Object.keys(app.packages).length === 0) {
      delete app.packages;
      app.status = "coming-soon";
    } else if (app.packages) {
      app.version = version;
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
    const body = await readFile(file);
    const kbps = Number(process.env.THROTTLE_KBPS ?? 0);
    if (!kbps || !path.startsWith("/packages/")) return res.end(body);
    // Trickle the package out so download progress and cancel can be watched.
    const step = Math.max(1, Math.floor((kbps * 1024) / 10));
    for (let i = 0; i < body.length && !res.destroyed; i += step) {
      res.write(body.subarray(i, i + step));
      await new Promise((r) => setTimeout(r, 100));
    }
    res.end();
  } catch {
    res.writeHead(404).end("not found");
  }
}).listen(port, "127.0.0.1", () => console.log(`dev catalog: ${base}/catalog.json`));
