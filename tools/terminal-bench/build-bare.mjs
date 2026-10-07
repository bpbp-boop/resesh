// Builds a bare xterm.js page (xterm + WebGL + unicode11 only, no resesh addons) that accepts the
// same host messages as the real page, to separate xterm/Chromium cost from our addon cost.
// usage: node build-bare.mjs <wwwroot> <out.html>
import fs from "node:fs";
import path from "node:path";

const [, , wwwroot, out] = process.argv;
const read = f => fs.readFileSync(path.join(wwwroot, f), "utf8");
const html = `<!doctype html><html><head><meta charset="utf-8">
<style>${read("xterm.css")} html,body{margin:0;background:#0c0c0c;overflow:hidden}</style>
<script>${read("xterm.js")}</script>
<script>${read("addon-webgl.js")}</script>
<script>${read("addon-unicode11.js")}</script>
</head><body><div id="t"></div>
<script>
const term = window.__term = new Terminal({ cols: 170, rows: 50, scrollback: 10000, allowProposedApi: true,
  fontFamily: "Cascadia Mono, Consolas, monospace", fontSize: 14 });
term.open(document.getElementById("t"));
term.loadAddon(new WebglAddon.WebglAddon());
term.loadAddon(new Unicode11Addon.Unicode11Addon());
term.unicode.activeVersion = "11";
window.__send = function (msg) {
  if (msg.type === "output") term.write(Uint8Array.fromBase64(msg.data));
};
window.__ready = { type: "ready", bare: true };
</script></body></html>`;
fs.writeFileSync(out, html);
console.log("bytes:", html.length);
