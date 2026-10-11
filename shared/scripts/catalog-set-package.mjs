// Updates an app's entry in catalog/catalog.json from the package ZIPs in
// ./packages: version, download URLs, SHA-256, sizes and release date.
//
//   node shared/scripts/catalog-set-package.mjs archaea-photo 0.1.0
//
// Run it after building the packages you are about to upload; the catalog must
// describe exactly the files in the release, byte for byte.

import { createHash } from "node:crypto";
import { readFile, writeFile } from "node:fs/promises";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..");
const [id, version] = process.argv.slice(2);
if (!id || !version) {
  console.error("usage: catalog-set-package.mjs <app-id> <version>");
  process.exit(1);
}

// Sum of the uncompressed sizes in the ZIP's central directory.
function installedSize(zip) {
  let end = zip.length - 22;
  while (end >= 0 && zip.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error("not a ZIP file");
  const count = zip.readUInt16LE(end + 10);
  let at = zip.readUInt32LE(end + 16);
  let total = 0;
  for (let i = 0; i < count; i++) {
    total += zip.readUInt32LE(at + 24);
    at += 46 + zip.readUInt16LE(at + 28) + zip.readUInt16LE(at + 30) + zip.readUInt16LE(at + 32);
  }
  return total;
}

const file = join(root, "catalog", "catalog.json");
const catalog = JSON.parse(await readFile(file, "utf8"));
const app = catalog.apps.find((a) => a.id === id);
if (!app) throw new Error(`${id} is not in the catalog`);

const packages = {};
for (const arch of ["x64", "arm64"]) {
  const name = `${id}-${version}-${arch}.zip`;
  let bytes;
  try {
    bytes = await readFile(join(root, "packages", name));
  } catch {
    console.warn(`packages/${name} not found; ${arch} left out`);
    continue;
  }
  packages[arch] = {
    url: `https://github.com/GavinVu/ArchaeaSuite/releases/download/${id}-v${version}/${name}`,
    sha256: createHash("sha256").update(bytes).digest("hex"),
    size: bytes.length,
    installedSize: installedSize(bytes),
  };
}
if (!packages.x64) throw new Error("an x64 package is required");

const today = new Date().toISOString().slice(0, 10);
app.status = "available";
app.version = version;
app.releaseDate = today;
app.packages = packages;
catalog.updated = today;
await writeFile(file, JSON.stringify(catalog, null, 2) + "\n");
console.log(`${id} ${version}:`, Object.keys(packages).join(", "));
