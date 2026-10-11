// Derives every brand asset from shared/brand/ArchaeaSuiteLogo.png:
//   public/brand/mark.png          the emblem, original colours (light backgrounds)
//   public/brand/mark-inverse.png  the emblem with black swapped for off-white (dark backgrounds)
//   src-tauri/app-icon.png         1024px source for `npm run tauri icon`
//   ../apps/ArchaeaPhoto/.../Assets  ArchaeaPhoto emblem and icon (from ArchaeaPhotoLogo.png)
//   ../catalog/icons/*.png           icons for the sample catalog
//   ../apps/ArchaeaIDE/...           ArchaeaIDE emblem, icon source and catalog icon (from ArchaeaIDELogo.png)
//
// Run with `npm run brand`, then `npm run tauri icon src-tauri/app-icon.png`.

import sharp from "sharp";
import { mkdir, writeFile } from "node:fs/promises";
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
const logo = sharp(resolve(root, "../shared/brand/ArchaeaSuiteLogo.png")).ensureAlpha();
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
  await sharp(Buffer.from(svg)).png({ compressionLevel: 9 }).toFile(await out(`../catalog/icons/${id}.png`));
}

// --- 4. ArchaeaPhoto: emblem, application icon and catalog icon -------------
// The Photo logo adds off-white details to the black/lime emblem, so each pixel
// is split into black, lime and white parts before recolouring.
{
  const photoDir = "../apps/ArchaeaPhoto/src/ArchaeaPhoto/Assets";
  const src = sharp(resolve(root, "../shared/brand/ArchaeaPhotoLogo.png")).ensureAlpha();
  const { data: px, info: pi } = await src.raw().toBuffer({ resolveWithObject: true });
  const W = pi.width, H = pi.height, C = pi.channels;
  const ink = (y) => {
    for (let x = 0; x < W; x++) if (px[(y * W + x) * C + 3] > 24) return true;
    return false;
  };
  let t = 0;
  while (t < H && !ink(t)) t++;
  let bt = t;
  for (let y = t, gap = 0; y < H; y++) {
    if (ink(y)) { bt = y; gap = 0; } else if (++gap > 12) break;
  }
  let l = W, r = 0;
  for (let y = t; y <= bt; y++)
    for (let x = 0; x < W; x++)
      if (px[(y * W + x) * C + 3] > 24) { if (x < l) l = x; if (x > r) r = x; }
  const pbox = { left: l, top: t, width: r - l + 1, height: bt - t + 1 };
  console.log("photo emblem bounds:", pbox);
  const pside = Math.max(pbox.width, pbox.height);

  const tint = (dark, light) => {
    const copy = Buffer.from(px);
    for (let i = 0; i < copy.length; i += C) {
      const w = copy[i + 2] / 255; // lime has no blue, so blue measures white
      const lime = Math.max(0, Math.min(1 - w, copy[i + 1] / 255 - w));
      const k = Math.max(0, 1 - w - lime);
      for (let c = 0; c < 3; c++) copy[i + c] = Math.round(dark[c] * k + LIME[c] * lime + light[c] * w);
    }
    return sharp(copy, { raw: { width: W, height: H, channels: C } })
      .extract(pbox)
      .extend({
        top: Math.floor((pside - pbox.height) / 2), bottom: Math.ceil((pside - pbox.height) / 2),
        left: Math.floor((pside - pbox.width) / 2), right: Math.ceil((pside - pbox.width) / 2),
        background: { r: 0, g: 0, b: 0, alpha: 0 },
      })
      .png()
      .toBuffer();
  };
  const photoInk = await tint(INK, PAPER);
  const photoPaper = await tint(PAPER, INK);
  await sharp(photoInk).resize(512, 512).png().toFile(await out(`${photoDir}/mark.png`));
  await sharp(photoPaper).resize(512, 512).png().toFile(await out(`${photoDir}/mark-inverse.png`));

  // Same tile as the launcher icon so the suite reads as one family.
  const photoEmblem = await sharp(photoInk).resize(760, 760).png().toBuffer();
  const photoIcon = await sharp(tile).composite([{ input: photoEmblem, gravity: "centre" }]).png().toBuffer();
  await sharp(photoIcon).resize(256, 256).png({ compressionLevel: 9 }).toFile(await out("../catalog/icons/archaea-photo.png"));
  await sharp(photoIcon).resize(256, 256).png().toFile(await out(`${photoDir}/icon.png`));

  // .ico with PNG-compressed entries (supported since Windows Vista).
  const sizes = [16, 24, 32, 48, 64, 128, 256];
  const pngs = await Promise.all(sizes.map((n) => sharp(photoIcon).resize(n, n).png().toBuffer()));
  const header = Buffer.alloc(6 + 16 * sizes.length);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  sizes.forEach((n, i) => {
    const e = 6 + 16 * i;
    header.writeUInt8(n === 256 ? 0 : n, e);
    header.writeUInt8(n === 256 ? 0 : n, e + 1);
    header.writeUInt16LE(1, e + 4);
    header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(pngs[i].length, e + 8);
    header.writeUInt32LE(offset, e + 12);
    offset += pngs[i].length;
  });
  await writeFile(await out(`${photoDir}/icon.ico`), Buffer.concat([header, ...pngs]));
}

// --- 5. Installer artwork ---------------------------------------------------
// NSIS only reads uncompressed 24-bit BMP: a 164x314 sidebar for the welcome
// and finish pages and a 150x57 header for the pages in between.
{
  const bmp = async (image, w, h) => {
    const rgb = await image.flatten({ background: "#161616" }).removeAlpha().raw().toBuffer();
    const stride = (w * 3 + 3) & ~3;
    const file = Buffer.alloc(54 + stride * h);
    file.write("BM");
    file.writeUInt32LE(file.length, 2);
    file.writeUInt32LE(54, 10);
    file.writeUInt32LE(40, 14);
    file.writeInt32LE(w, 18);
    file.writeInt32LE(h, 22); // positive height: rows are stored bottom-up
    file.writeUInt16LE(1, 26);
    file.writeUInt16LE(24, 28);
    file.writeUInt32LE(stride * h, 34);
    for (let y = 0; y < h; y++)
      for (let x = 0; x < w; x++) {
        const s = (y * w + x) * 3, d = 54 + (h - 1 - y) * stride + x * 3;
        file[d] = rgb[s + 2];
        file[d + 1] = rgb[s + 1];
        file[d + 2] = rgb[s];
      }
    return file;
  };
  const art = async (w, h, markSize, markTop, bar) => {
    const ground = Buffer.from(
      `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}">
         <rect width="${w}" height="${h}" fill="#161616"/>
         <rect y="${h - bar}" width="${w}" height="${bar}" fill="#D6FF00"/>
       </svg>`
    );
    const mark = await sharp(markPaper).resize(markSize, markSize).png().toBuffer();
    return bmp(
      sharp(ground).composite([{ input: mark, left: Math.round((w - markSize) / 2), top: markTop }]),
      w,
      h
    );
  };
  await writeFile(await out("src-tauri/installer/sidebar.bmp"), await art(164, 314, 116, 70, 12));
  await writeFile(await out("src-tauri/installer/header.bmp"), await art(150, 57, 44, 4, 4));
}

// --- 6. ArchaeaIDE: emblem, application icon and catalog icon ---------------
// ArchaeaIDELogo.png is opaque: a charcoal emblem on black with a lime core
// and off-white brackets. Each pixel is split into charcoal, lime and white
// coverage (black becomes transparent) and then recoloured like the others.
{
  const ideDir = "../apps/ArchaeaIDE";
  const { data: px, info: pi } = await sharp(resolve(root, "../shared/brand/ArchaeaIDELogo.png"))
    .removeAlpha()
    .raw()
    .toBuffer({ resolveWithObject: true });
  const W = pi.width, H = pi.height;
  const clamp = (v) => Math.max(0, Math.min(1, v));
  const parts = new Float32Array(W * H * 3); // charcoal, lime, white
  const alpha = new Uint8Array(W * H);
  for (let i = 0; i < W * H; i++) {
    const g = px[i * 3 + 1], b = px[i * 3 + 2];
    const lime = clamp((g - b) / 235);
    const white = clamp((b - 40) / 190) * (1 - lime);
    const dark = clamp(clamp((b - 3) / 17) - white - lime);
    parts.set([dark, lime, white], i * 3);
    alpha[i] = Math.round(255 * clamp(dark + lime + white));
  }
  const ink = (y) => {
    for (let x = 0; x < W; x++) if (alpha[y * W + x] > 60) return true;
    return false;
  };
  let t = 0;
  while (t < H && !ink(t)) t++;
  let bt = t;
  for (let y = t, gap = 0; y < H; y++) {
    if (ink(y)) { bt = y; gap = 0; } else if (++gap > 12) break;
  }
  let l = W, r = 0;
  for (let y = t; y <= bt; y++)
    for (let x = 0; x < W; x++)
      if (alpha[y * W + x] > 60) { if (x < l) l = x; if (x > r) r = x; }
  const ibox = { left: l, top: t, width: r - l + 1, height: bt - t + 1 };
  console.log("ide emblem bounds:", ibox);
  const iside = Math.max(ibox.width, ibox.height);

  const tint = (dark, light) => {
    const rgba = Buffer.alloc(W * H * 4);
    for (let i = 0; i < W * H; i++) {
      const [k, lime, w] = parts.subarray(i * 3, i * 3 + 3);
      const sum = k + lime + w || 1;
      for (let c = 0; c < 3; c++) rgba[i * 4 + c] = Math.round((dark[c] * k + LIME[c] * lime + light[c] * w) / sum);
      rgba[i * 4 + 3] = alpha[i];
    }
    return sharp(rgba, { raw: { width: W, height: H, channels: 4 } })
      .extract(ibox)
      .extend({
        top: Math.floor((iside - ibox.height) / 2), bottom: Math.ceil((iside - ibox.height) / 2),
        left: Math.floor((iside - ibox.width) / 2), right: Math.ceil((iside - ibox.width) / 2),
        background: { r: 0, g: 0, b: 0, alpha: 0 },
      })
      .png()
      .toBuffer();
  };
  const ideInk = await tint(INK, PAPER);
  await sharp(ideInk).resize(512, 512).png().toFile(await out(`${ideDir}/public/brand/mark.png`));
  await sharp(await tint(PAPER, INK)).resize(512, 512).png().toFile(await out(`${ideDir}/public/brand/mark-inverse.png`));
  const ideIcon = await sharp(tile)
    .composite([{ input: await sharp(ideInk).resize(760, 760).png().toBuffer(), gravity: "centre" }])
    .png()
    .toBuffer();
  await sharp(ideIcon).toFile(await out(`${ideDir}/src-tauri/app-icon.png`));
  await sharp(ideIcon).resize(256, 256).png({ compressionLevel: 9 }).toFile(await out("../catalog/icons/archaea-ide.png"));
}

console.log("brand assets written");
