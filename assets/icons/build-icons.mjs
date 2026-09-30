// Renders every SVG in ./svg to PNGs (png/<name>-<size>.png) and packs a multi-size .ico (ico/<name>.ico).
// ICO entries are PNG-compressed, which Windows Vista and later read natively.
import { Resvg } from '@resvg/resvg-js';
import { readdirSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { join, basename, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const svgDir = join(here, 'svg');
const pngDir = join(here, 'png');
const icoDir = join(here, 'ico');
mkdirSync(pngDir, { recursive: true });
mkdirSync(icoDir, { recursive: true });

// App/tray icons get every size Windows asks for; UI glyphs get the sizes the toolbar and menus use.
const appIcons = new Set(['novaget', 'tray-active']);
const appSizes = [16, 20, 24, 32, 40, 48, 64, 256];
const uiSizes = [16, 24, 32, 48, 64, 96];

function render(svg, size) {
  const resvg = new Resvg(svg, { fitTo: { mode: 'width', value: size }, background: 'rgba(0,0,0,0)' });
  return resvg.render().asPng();
}

function packIco(images) {
  const count = images.length;
  const header = Buffer.alloc(6 + 16 * count);
  header.writeUInt16LE(0, 0);
  header.writeUInt16LE(1, 2);
  header.writeUInt16LE(count, 4);
  let offset = header.length;
  images.forEach(({ size, png }, i) => {
    const e = 6 + 16 * i;
    header.writeUInt8(size >= 256 ? 0 : size, e);
    header.writeUInt8(size >= 256 ? 0 : size, e + 1);
    header.writeUInt8(0, e + 2);
    header.writeUInt8(0, e + 3);
    header.writeUInt16LE(1, e + 4);
    header.writeUInt16LE(32, e + 6);
    header.writeUInt32LE(png.length, e + 8);
    header.writeUInt32LE(offset, e + 12);
    offset += png.length;
  });
  return Buffer.concat([header, ...images.map((x) => x.png)]);
}

for (const file of readdirSync(svgDir).filter((f) => f.endsWith('.svg')).sort()) {
  const name = basename(file, '.svg');
  const svg = readFileSync(join(svgDir, file));
  const sizes = appIcons.has(name) ? appSizes : uiSizes;
  const images = sizes.map((size) => ({ size, png: render(svg, size) }));
  for (const { size, png } of images) {
    writeFileSync(join(pngDir, `${name}-${size}.png`), png);
  }
  if (appIcons.has(name)) {
    writeFileSync(join(icoDir, `${name}.ico`), packIco(images));
  }
  console.log(`${name}: ${sizes.join(', ')}`);
}
