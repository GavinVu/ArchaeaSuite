// Derives every brand asset from ArchaeaSuiteLogo.png:
//   public/brand/mark.png          the emblem, original colours (light backgrounds)
//   public/brand/mark-inverse.png  the emblem with black swapped for off-white (dark backgrounds)
//   src-tauri/app-icon.png         1024px source for `npm run tauri icon`
//   catalog/icons/*.png            icons for the sample catalog
//
// Run with `npm run brand`, then `npm run tauri icon src-tauri/app-icon.png`.

import sharp from "sharp";
import { mkdir } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const INK = [0x16, 0x16, 0x16];
const PAPER = [0xf5, 0xf5, 0xf0];
const LIME = [0xd6, 0xff, 0x00];

const out = async (rel) => {
  const path = resolve(root, rel);
  await mkdir(dirname(path), { recursive: true });
  return path;
};

// --- 1. Isolate the emblem (everything above the wordmark) -----------------
const logo = sharp(resolve(root, "ArchaeaSuiteLogo.png")).ensureAlpha();
const { data, info } = await logo.raw().toBuffer({ resolveWithObject: true });
const { width, height, channels } = info;

const rowHasInk = (y) => {
  for (let x = 0; x < width; x++) if (data[(y * width + x) * channels + 3] > 24) return true;
  return false;
};
let top = 0;
while (top < height && !rowHasInk(top)) top++;
// The emblem ends at the first run of empty rows below it.
let bottom = top;
for (let y = top, gap = 0; y < height; y++) {
  if (rowHasInk(y)) {
    bottom = y;
    gap = 0;
  } else if (++gap > 12) break;
}
let left = width, right = 0;
for (let y = top; y <= bottom; y++) {
  for (let x = 0; x < width; x++) {
    if (data[(y * width + x) * channels + 3] > 24) {
      if (x < left) left = x;
      if (x > right) right = x;
    }
  }
}
const box = { left, top, width: right - left + 1, height: bottom - top + 1 };
console.log("emblem bounds:", box);

const side = Math.max(box.width, box.height);
const squareMark = async (buffer) =>
  sharp(buffer, { raw: { width, height, channels } })
    .extract(box)
    .extend({
      top: Math.floor((side - box.height) / 2),
      bottom: Math.ceil((side - box.height) / 2),
      left: Math.floor((side - box.width) / 2),
      right: Math.ceil((side - box.width) / 2),
      background: { r: 0, g: 0, b: 0, alpha: 0 },
    })
    .png()
    .toBuffer();

// Recolour: every pixel is a blend of black and lime; `t` is how lime it is.
// Mapping black -> `ink` while keeping lime gives clean anti-aliased edges.
const recolour = (ink) => {
  const copy = Buffer.from(data);
  for (let i = 0; i < copy.length; i += channels) {
    const t = Math.min(1, copy[i + 1] / 255);
    for (let c = 0; c < 3; c++) copy[i + c] = Math.round(ink[c] + t * (LIME[c] - ink[c]));
  }
  return copy;
};

const markInk = await squareMark(recolour(INK));
const markPaper = await squareMark(recolour(PAPER));

await sharp(markInk).resize(512, 512).png().toFile(await out("public/brand/mark.png"));
await sharp(markPaper).resize(512, 512).png().toFile(await out("public/brand/mark-inverse.png"));

// --- 2. Application icon: emblem on an off-white tile ----------------------
const ICON = 1024;
const tile = Buffer.from(
  `<svg xmlns="http://www.w3.org/2000/svg" width="${ICON}" height="${ICON}">
     <rect x="40" y="40" width="${ICON - 80}" height="${ICON - 80}" rx="150" fill="#F5F5F0" stroke="#161616" stroke-width="36"/>
   </svg>`
);
const emblem = await sharp(markInk).resize(700, 700).png().toBuffer();
await sharp(tile)
  .composite([{ input: emblem, gravity: "centre" }])
  .png()
  .toFile(await out("src-tauri/app-icon.png"));

// --- 3. Sample catalog icons -----------------------------------------------
// Flat lime glyphs on a near-black tile; one per planned application.
const glyphs = {
  "archaea-hello": `<path d="M76 92 L124 128 L76 164" fill="none" stroke="#D6FF00" stroke-width="20" stroke-linejoin="miter"/><rect x="132" y="148" width="52" height="20" fill="#D6FF00"/>`,
  "archaea-photo": `<rect x="62" y="70" width="132" height="116" fill="none" stroke="#D6FF00" stroke-width="16"/><circle cx="128" cy="128" r="30" fill="#D6FF00"/><rect x="160" y="84" width="16" height="16" fill="#D6FF00"/>`,
  "archaea-vector": `<path d="M70 186 C 70 100, 150 70, 186 70" fill="none" stroke="#D6FF00" stroke-width="16"/><rect x="54" y="170" width="32" height="32" fill="#D6FF00"/><rect x="170" y="54" width="32" height="32" fill="#D6FF00"/><circle cx="104" cy="104" r="12" fill="#D6FF00"/>`,
  "archaea-cut": `<rect x="56" y="78" width="96" height="36" fill="#D6FF00"/><rect x="104" y="142" width="96" height="36" fill="#D6FF00"/><rect x="56" y="142" width="32" height="36" fill="none" stroke="#D6FF00" stroke-width="8"/><rect x="120" y="56" width="12" height="144" fill="#F5F5F0"/>`,
  "archaea-motion": `<circle cx="92" cy="128" r="38" fill="none" stroke="#D6FF00" stroke-width="8" opacity="0.45"/><circle cx="128" cy="128" r="38" fill="none" stroke="#D6FF00" stroke-width="12" opacity="0.7"/><circle cx="164" cy="128" r="38" fill="#D6FF00"/>`,
  "archaea-layout": `<rect x="64" y="56" width="128" height="144" fill="none" stroke="#D6FF00" stroke-width="16"/><rect x="88" y="82" width="80" height="34" fill="#D6FF00"/><rect x="88" y="132" width="34" height="44" fill="#D6FF00"/><rect x="134" y="132" width="34" height="12" fill="#D6FF00"/><rect x="134" y="154" width="34" height="12" fill="#D6FF00"/>`,
  "archaea-audio": [60, 86, 112, 138, 164, 190]
    .map((x, i) => {
      const h = [48, 112, 76, 136, 60, 96][i];
      return `<rect x="${x - 7}" y="${128 - h / 2}" width="14" height="${h}" fill="#D6FF00"/>`;
    })
    .join(""),
};

for (const [id, glyph] of Object.entries(glyphs)) {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
    <rect x="6" y="6" width="244" height="244" rx="28" fill="#161616" stroke="#D6FF00" stroke-width="6"/>
    ${glyph}
  </svg>`;
  await sharp(Buffer.from(svg)).png({ compressionLevel: 9 }).toFile(await out(`catalog/icons/${id}.png`));
}

console.log("brand assets written");
