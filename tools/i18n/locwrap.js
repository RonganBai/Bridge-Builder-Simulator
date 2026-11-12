// Wrap Chinese string literals in C# files: "中文" -> Loc.T("中文"), $"中{x:0}" -> Loc.F("中{0:0}", (x))
const fs = require("fs"), path = require("path");
const han = /[一-鿿！-～、-】]/;
const keys = { plain: new Set(), format: new Set() };

function walk(d, out = []) { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p, out); else if (p.endsWith(".cs")) out.push(p); } return out; }

// Read a normal or verbatim string starting at i (s[i] is the opening quote). Returns end index (exclusive).
function readString(s, i, verbatim) {
  let j = i + 1;
  while (j < s.length) {
    if (verbatim) { if (s[j] === '"') { if (s[j + 1] === '"') { j += 2; continue; } return j + 1; } j++; }
    else { if (s[j] === "\\") { j += 2; continue; } if (s[j] === '"') return j + 1; if (s[j] === "\n") throw new Error("newline in string"); j++; }
  }
  throw new Error("unterminated string");
}

// Read an interpolated string body starting at the opening quote; returns {end, parts:[{text}|{expr,align,fmt}]}
function readInterp(s, i, verbatim) {
  let j = i + 1, text = "", parts = [];
  while (j < s.length) {
    const c = s[j];
    if (c === "{") {
      if (s[j + 1] === "{") { text += "{{"; j += 2; continue; }
      if (text) { parts.push({ text }); text = ""; }
      // hole
      let k = j + 1, depth = 0, exprEnd = -1, alignStart = -1, fmtStart = -1;
      while (k < s.length) {
        const d = s[k];
        if (d === '"' || (d === "@" && s[k + 1] === '"') || (d === "$" && (s[k + 1] === '"' || s[k + 1] === "@"))) {
          // nested string inside hole
          if (d === '"') { k = readString(s, k, false); continue; }
          if (d === "@") { k = readString(s, k + 1, true); continue; }
          let v = false, q = k + 1; if (s[q] === "@") { v = true; q++; }
          k = readInterp(s, q, v).end; continue;
        }
        if (d === "'") { k = s[k + 1] === "\\" ? s.indexOf("'", k + 3) + 1 : k + 3; continue; }
        if ("([{".includes(d)) depth++;
        else if (")]".includes(d)) depth--;
        else if (d === "}") { if (depth === 0) break; depth--; }
        else if (d === "," && depth === 0 && alignStart < 0 && fmtStart < 0) { alignStart = k; }
        else if (d === ":" && depth === 0 && fmtStart < 0) {
          // "::" alias qualifier / ternary inside parens are excluded by depth; a top-level ':' starts the format
          fmtStart = k;
        }
        k++;
      }
      const end = k; // s[end] === '}'
      const exprEnd2 = alignStart >= 0 ? alignStart : fmtStart >= 0 ? fmtStart : end;
      parts.push({ expr: s.slice(j + 1, exprEnd2), align: alignStart >= 0 ? s.slice(alignStart, fmtStart >= 0 ? fmtStart : end) : "", fmt: fmtStart >= 0 ? s.slice(fmtStart, end) : "" });
      j = end + 1; continue;
    }
    if (c === "}") { if (s[j + 1] === "}") { text += "}}"; j += 2; continue; } throw new Error("lone }"); }
    if (verbatim) { if (c === '"') { if (s[j + 1] === '"') { text += '""'; j += 2; continue; } break; } text += c; j++; }
    else { if (c === "\\") { text += s.slice(j, j + 2); j += 2; continue; } if (c === '"') break; if (c === "\n") throw new Error("newline in interp"); text += c; j++; }
  }
  if (text) parts.push({ text });
  return { end: j + 1, parts };
}

function unescape(lit) { // C# literal body -> actual text (for the key list)
  return lit.replace(/\\u([0-9a-fA-F]{4})/g, (_, h) => String.fromCharCode(parseInt(h, 16))).replace(/\\n/g, "\n").replace(/\\t/g, "\t").replace(/\\"/g, '"').replace(/\\\\/g, "\\");
}

function transform(src) {
  let out = "", i = 0;
  while (i < src.length) {
    const c = src[i];
    if (c === "/" && src[i + 1] === "/") { const e = src.indexOf("\n", i); const end = e < 0 ? src.length : e; out += src.slice(i, end); i = end; continue; }
    if (c === "/" && src[i + 1] === "*") { const e = src.indexOf("*/", i + 2) + 2; out += src.slice(i, e); i = e; continue; }
    if (c === "'") { let e = src[i + 1] === "\\" ? src.indexOf("'", i + 3) + 1 : i + 3; out += src.slice(i, e); i = e; continue; }
    const before = out.slice(-6);
    if (c === '"' || (c === "@" && src[i + 1] === '"')) {
      const verb = c === "@"; const q = verb ? i + 1 : i; const end = readString(src, q, verb);
      const lit = src.slice(i, end);
      const body = src.slice(q + 1, end - 1);
      if (han.test(body) && !/Loc\.[TF]\($/.test(out)) { out += "Loc.T(" + lit + ")"; keys.plain.add(verb ? body.replace(/""/g, '"') : unescape(body)); }
      else out += lit;
      i = end; continue;
    }
    if (c === "$" && (src[i + 1] === '"' || (src[i + 1] === "@" && src[i + 2] === '"'))) {
      const verb = src[i + 1] === "@"; const q = verb ? i + 2 : i + 1;
      const r = readInterp(src, q, verb);
      const raw = src.slice(i, r.end);
      const textAll = r.parts.filter(p => p.text).map(p => p.text).join("");
      if (!han.test(textAll)) {
        // no Chinese in the fixed text: only transform literals inside the holes
        let rebuilt = (verb ? "$@\"" : "$\"") + r.parts.map(p => p.text !== undefined ? p.text : "{" + transform(p.expr) + p.align + p.fmt + "}").join("") + '"';
        out += rebuilt; i = r.end; continue;
      }
      let fmt = "", args = [], n = 0;
      for (const p of r.parts) {
        if (p.text !== undefined) fmt += p.text;
        else { fmt += "{" + n++ + p.align + p.fmt + "}"; args.push("(" + transform(p.expr).trim() + ")"); }
      }
      const fmtLit = (verb ? "@\"" : "\"") + fmt + '"';
      keys.format.add(verb ? fmt.replace(/""/g, '"') : unescape(fmt));
      out += args.length ? "Loc.F(" + fmtLit + ", " + args.join(", ") + ")" : "Loc.T(" + fmtLit + ")";
      i = r.end; continue;
    }
    out += c; i++;
  }
  return out;
}

const root = process.argv[2];
const dry = process.argv.includes("--dry");
let changed = 0;
for (const f of walk(path.join(root, "Scripts"))) {
  if (f.endsWith(path.join("Data", "Loc.cs"))) continue;
  const orig = fs.readFileSync(f, "utf8");
  let s;
  try { s = transform(orig); } catch (e) { console.error("FAIL", f, e.message); process.exit(1); }
  if (s === orig) continue;
  changed++;
  if (!/using BridgeArchitect3D\.Data;/.test(s) && !/namespace BridgeArchitect3D\.Data\b/.test(s)) {
    const m = s.match(/^(﻿?)((?:using [^\n]*\n)*)/);
    s = s.replace(/^(﻿?)/, "$1using BridgeArchitect3D.Data;" + (s.includes("\r\n") ? "\r\n" : "\n"));
  }
  if (!dry) fs.writeFileSync(f, s);
}
fs.writeFileSync(process.env.TEMP + "/lockeys.json", JSON.stringify({ plain: [...keys.plain], format: [...keys.format] }, null, 1));
console.log("files", changed, "plain", keys.plain.size, "format", keys.format.size);
