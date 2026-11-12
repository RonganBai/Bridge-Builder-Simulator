// Lists texts that have no English entry in Data/Locale/en.json:
//  - first arguments of Loc.T("...") / Loc.F("...") in Scripts/**/*.cs
//  - Chinese strings in the level / material / vehicle data
//  - Chinese literals not wrapped in Loc.T / Loc.F yet (run tools/i18n/locwrap.js to wrap them)
// Usage (project root): node tools/i18n/missing.js
const fs = require("fs"), path = require("path"), cp = require("child_process");
const han = /[一-鿿]/; const all = new Set();
function walk(d, out = []) { for (const e of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, e.name); if (e.isDirectory()) walk(p, out); else if (p.endsWith(".cs")) out.push(p); } return out; }
const unesc = s => s.replace(/\n/g, "\n").replace(/\t/g, "\t").replace(/\\"/g, '"').replace(/\\/g, "\\");
for (const f of walk("Scripts")) {
  const s = fs.readFileSync(f, "utf8"); const re = /Loc\.[TF]\(\s*(@?)"((?:[^"\\n]|\.)*)"/g; let m;
  while ((m = re.exec(s))) all.add(m[1] ? m[2].replace(/""/g, '"') : unesc(m[2]));
}
function visit(v) { if (typeof v === "string") { if (han.test(v)) all.add(v); } else if (v && typeof v === "object") for (const x of Object.values(v)) visit(x); }
for (const f of ["Data/Levels/levels.json", "Data/Materials/materials.json", "Data/Vehicles/vehicles.json"]) { const j = JSON.parse(fs.readFileSync(f, "utf8").replace(/^﻿/, "")); delete j._units; visit(j); }
const en = JSON.parse(fs.readFileSync("Data/Locale/en.json", "utf8"));
const missing = [...all].filter(k => !(k in en));
missing.forEach(k => console.log("missing: " + JSON.stringify(k)));
cp.execFileSync(process.execPath, [path.join(__dirname, "locwrap.js"), ".", "--dry"], { stdio: "ignore" });
const unwrapped = JSON.parse(fs.readFileSync(path.join(process.env.TEMP, "lockeys.json"), "utf8"));
[...unwrapped.plain, ...unwrapped.format].forEach(k => console.log("not wrapped: " + JSON.stringify(k)));
console.log(`${missing.length} missing of ${all.size}; ${unwrapped.plain.length + unwrapped.format.length} not wrapped`);
