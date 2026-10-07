// Summarize a .cpuprofile: self time by function+url:line, and inclusive time for top functions.
// usage: node top.mjs <file.cpuprofile> [n]
import fs from "node:fs";
const [, , file, n = "35"] = process.argv;
const p = JSON.parse(fs.readFileSync(file, "utf8"));
const byId = new Map(p.nodes.map(x => [x.id, x]));
const parent = new Map();
for (const x of p.nodes) for (const c of x.children || []) parent.set(c, x.id);
const counts = new Map();
for (const s of p.samples) counts.set(s, (counts.get(s) || 0) + 1);
const dt = (p.endTime - p.startTime) / p.samples.length / 1000; // ms per sample
const key = x => `${x.callFrame.functionName || "(anon)"} ${(x.callFrame.url || "").split("/").pop().slice(0, 20)}:${x.callFrame.lineNumber}:${x.callFrame.columnNumber}`;
const self = new Map(), incl = new Map();
let total = 0;
for (const [id, c] of counts) {
  const x = byId.get(id); total += c;
  self.set(key(x), (self.get(key(x)) || 0) + c);
  const seen = new Set();
  for (let cur = id; cur !== undefined; cur = parent.get(cur)) {
    const k = key(byId.get(cur)); if (seen.has(k)) continue; seen.add(k);
    incl.set(k, (incl.get(k) || 0) + c);
  }
}
const fmt = m => [...m].sort((a, b) => b[1] - a[1]).slice(0, +n).map(([k, c]) => `${(c * dt).toFixed(0).padStart(7)}ms ${(100 * c / total).toFixed(1).padStart(5)}%  ${k}`).join("\n");
console.log(`total ${(total * dt).toFixed(0)}ms\n--- self ---\n${fmt(self)}\n--- inclusive ---\n${fmt(incl)}`);
