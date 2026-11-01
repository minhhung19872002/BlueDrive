/**
 * Generates the BlueDrive app icons / splash image (solid #0b5394 with a white "B").
 * Pure Node (zlib only) — no extra dependencies. Run: node scripts/generate-icons.js
 */
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const BRAND = [0x0b, 0x53, 0x94];
const WHITE = [0xff, 0xff, 0xff];
const OUT = path.join(__dirname, '..', 'assets');

// ---------- PNG encoder ----------
const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const len = Buffer.alloc(4);
  len.writeUInt32BE(data.length);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(td));
  return Buffer.concat([len, td, crc]);
}

function encodePng(size, rgba) {
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // RGBA
  const raw = Buffer.alloc((size * 4 + 1) * size);
  for (let y = 0; y < size; y++) {
    raw[y * (size * 4 + 1)] = 0;
    rgba.copy(raw, y * (size * 4 + 1) + 1, y * size * 4, (y + 1) * size * 4);
  }
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', zlib.deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ---------- "B" glyph in unit box (x: 0..0.62, y: 0..1) ----------
function inEllipse(x, y, cx, cy, rx, ry) {
  const dx = (x - cx) / rx;
  const dy = (y - cy) / ry;
  return dx * dx + dy * dy <= 1;
}

function inB(x, y) {
  if (x < 0 || y < 0 || y > 1) return false;
  // Stem + two bowls (upper bowl slightly narrower than the lower one).
  const outer = x <= 0.3 || (y <= 0.5 ? inEllipse(x, y, 0.3, 0.25, 0.27, 0.25) : inEllipse(x, y, 0.3, 0.75, 0.32, 0.25));
  if (!outer) return false;
  const upperHole = x > 0.16 && y > 0.14 && y < 0.38 && (x <= 0.3 || inEllipse(x, y, 0.3, 0.26, 0.13, 0.12));
  const lowerHole = x > 0.16 && y > 0.6 && y < 0.88 && (x <= 0.3 || inEllipse(x, y, 0.3, 0.74, 0.17, 0.14));
  return !upperHole && !lowerHole;
}

/**
 * @param size pixel size
 * @param bg background RGB or null (transparent)
 * @param fg glyph RGB
 * @param glyphHeight glyph height relative to the image
 */
function render(size, bg, fg, glyphHeight) {
  const buf = Buffer.alloc(size * size * 4);
  const gh = glyphHeight * size;
  const gw = gh * 0.62;
  const ox = (size - gw) / 2;
  const oy = (size - gh) / 2;
  const SS = 4;
  for (let py = 0; py < size; py++) {
    for (let px = 0; px < size; px++) {
      let hits = 0;
      for (let sy = 0; sy < SS; sy++) {
        for (let sx = 0; sx < SS; sx++) {
          const ux = (px + (sx + 0.5) / SS - ox) / gh;
          const uy = (py + (sy + 0.5) / SS - oy) / gh;
          if (inB(ux, uy)) hits++;
        }
      }
      const a = hits / (SS * SS);
      const i = (py * size + px) * 4;
      if (bg) {
        buf[i] = Math.round(bg[0] * (1 - a) + fg[0] * a);
        buf[i + 1] = Math.round(bg[1] * (1 - a) + fg[1] * a);
        buf[i + 2] = Math.round(bg[2] * (1 - a) + fg[2] * a);
        buf[i + 3] = 255;
      } else {
        buf[i] = fg[0];
        buf[i + 1] = fg[1];
        buf[i + 2] = fg[2];
        buf[i + 3] = Math.round(a * 255);
      }
    }
  }
  return encodePng(size, buf);
}

function solid(size, rgb) {
  const buf = Buffer.alloc(size * size * 4);
  for (let i = 0; i < size * size; i++) {
    buf[i * 4] = rgb[0];
    buf[i * 4 + 1] = rgb[1];
    buf[i * 4 + 2] = rgb[2];
    buf[i * 4 + 3] = 255;
  }
  return encodePng(size, buf);
}

fs.mkdirSync(OUT, { recursive: true });
const files = {
  'icon.png': render(1024, BRAND, WHITE, 0.56),
  'android-icon-foreground.png': render(1024, null, WHITE, 0.38),
  'android-icon-background.png': solid(1024, BRAND),
  'android-icon-monochrome.png': render(1024, null, WHITE, 0.38),
  'splash-icon.png': render(512, null, WHITE, 0.6),
  'favicon.png': render(48, BRAND, WHITE, 0.6),
};
for (const [name, data] of Object.entries(files)) {
  fs.writeFileSync(path.join(OUT, name), data);
  console.log('wrote', name, data.length, 'bytes');
}
