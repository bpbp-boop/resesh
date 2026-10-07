// Builds a self-contained harness page from a wwwroot dir, mirroring TerminalControl.LoadTerminalPage,
// with a WebView2 host stub and globals exposed for measurement.
// usage: node build.mjs <wwwroot> <out.html>
import fs from "node:fs";
import path from "node:path";

const [, , wwwroot, out] = process.argv;
let html = fs.readFileSync(path.join(wwwroot, "terminal.html"), "utf8");
const assets = [
  ['<link rel="stylesheet" href="xterm.css">', "xterm.css", "<style>", "</style>"],
  ...["xterm.js", "addon-webgl.js", "addon-unicode11.js", "addon-fit.js", "addon-web-links.js",
      "addon-search.js", "addon-highlight.js", "addon-serialize.js", "addon-ruler.js"]
    .map(f => [`<script src="${f}"></script>`, f, "<script>", "</script>"]),
];
for (const [marker, file, open, close] of assets) {
  const i = html.indexOf(marker);
  if (i < 0) throw new Error("missing " + file);
  const asset = fs.readFileSync(path.join(wwwroot, file), "utf8");
  html = html.slice(0, i) + open + asset + close + html.slice(i + marker.length);
}

// Extract builtin highlight rules from the C# source (all enabled by default except Enabled=false ones).
const cs = fs.readFileSync(path.join(wwwroot, "../../Core/Models/HighlightRule.cs"), "utf8");
const rules = [];
for (const m of cs.matchAll(/new\(\)\s*\{([\s\S]*?)\n\s*\},/g)) {
  const body = m[1];
  const id = /Id = "([^"]+)"/.exec(body)?.[1];
  const pat = /Pattern = @"((?:[^"]|"")*)"/.exec(body)?.[1]?.replace(/""/g, '"');
  const color = /Color = "([^"]+)"/.exec(body)?.[1];
  if (!id || !pat) continue;
  if (/Enabled = false/.test(body)) continue;
  rules.push({ id, name: id, pattern: pat, color, bold: /Bold = true/.test(body), underline: false,
    matchCase: /MatchCase = true/.test(body), showInOverview: /ShowInOverview = true/.test(body) });
}

const stub = `<script>
(function(){
  const listeners = [];
  window.__posted = [];
  window.__rules = ${JSON.stringify(rules)};
  window.__init = Object.assign({ type: "initOptions", fontSize: 14, fontFamily: "Cascadia Mono, Consolas, monospace",
    theme: "dark", copyOnSelect: true, rightClickPaste: true, scrollback: 10000, highlights: window.__rules,
    readOnly: false, fixed80Columns: false, shortcuts: [] }, JSON.parse(new URLSearchParams(location.search).get("init") || "{}"));
  window.__send = function (data) { const ev = new MessageEvent("message", { data }); listeners.forEach(l => l(ev)); };
  window.chrome = window.chrome || {};
  window.chrome.webview = {
    addEventListener(t, l) { if (t === "message") listeners.push(l); },
    removeEventListener() {},
    postMessage(m) {
      if (m && m.type === "init") setTimeout(() => window.__send(window.__init), 0);
      else if (m && m.type === "ready") window.__ready = m;
      if (m && m.type !== "keyframe") { window.__posted.push(m.type); if (window.__posted.length > 1000) window.__posted.splice(0, 500); }
      else window.__keyframes = (window.__keyframes || 0) + 1;
    }
  };
})();
</script>`;
const first = html.indexOf("<style>"); // before the first inlined asset
html = html.slice(0, first) + stub + html.slice(first);
html = html.replace("const term = new Terminal({", "const term = window.__term = new Terminal({");
html = html.replace("const ruler = new RulerAddon.RulerAddon();", "const ruler = window.__ruler = new RulerAddon.RulerAddon();");
if (!html.includes("window.__term") || !html.includes("window.__ruler")) throw new Error("patch failed");
fs.writeFileSync(out, html);
console.log("rules:", rules.length, "bytes:", html.length);
