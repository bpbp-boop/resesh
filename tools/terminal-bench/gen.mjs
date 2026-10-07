// Writes the benchmark workloads as raw UTF-8 byte files so the WebView harness and the
// native harness consume identical input. The generators match the 2026-10-06 output-path
// profiling rig byte for byte.
// usage: node gen.mjs <outDir>
import fs from "node:fs";
import path from "node:path";

export const MB = { logs: 8, color: 8, tiny: 2, tui: 8, unicode: 4 };

export function gen(kind, mb) {
  const target = mb * 1024 * 1024; const parts = []; let n = 0; let i = 0;
  const rnd = (k) => (Math.imul(i + 1, 2654435761) >>> 0) % k;
  while (n < target) {
    let s;
    if (kind === "logs") s = "2026-10-06T12:" + String(i % 60).padStart(2, "0") + ":01." + (i % 1000) + " INFO  [worker-" + (i % 16) + "] GET /api/v1/items/" + i + " from 10.0." + (i % 255) + "." + rnd(255) + " status=200 took=" + rnd(900) + "ms user=alice eth0 up\r\n" + (i % 50 === 0 ? "ERROR failed to connect to 192.168.1." + rnd(255) + ": connection refused (Gi0/" + rnd(48) + ")\r\n" : "");
    else if (kind === "color") s = "\x1b[01;34mdir" + i + "\x1b[0m  \x1b[01;32mexec_" + i + ".sh\x1b[0m  \x1b[38;5;" + rnd(255) + "mfile_" + i + ".txt\x1b[0m  \x1b[38;2;" + rnd(255) + ";" + rnd(255) + ";" + rnd(255) + "m truecolor " + i + "\x1b[0m drwxr-xr-x 2 root root 4096 Oct  6 12:00\r\n";
    else if (kind === "tiny") s = "y\r\n";
    else if (kind === "tui") {
      s = i === 0 ? "\x1b[?1049h" : "";
      // one frame: redraw 40 rows with cursor positioning, like htop/btop
      let f = "\x1b[H";
      for (let r = 1; r <= 40; r++) f += "\x1b[" + r + ";1H\x1b[3" + (r % 8) + "m" + (" PID " + (1000 + r) + " user   " + rnd(100) + "." + rnd(10) + "%  " + "x".repeat(rnd(60))).padEnd(120) + "\x1b[0m";
      s += f;
    }
    else if (kind === "unicode") s = "日本語のテキスト " + i + " ✓ 👍 ünïcödé ── │ ├── src/" + i + "\r\n";
    parts.push(s); n += s.length; i++;
  }
  return new TextEncoder().encode(parts.join(""));
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(new URL(import.meta.url).pathname.slice(1))) {
  const out = process.argv[2];
  if (!out) throw new Error("usage: node gen.mjs <outDir>");
  fs.mkdirSync(out, { recursive: true });
  for (const [kind, mb] of Object.entries(MB)) {
    const bytes = gen(kind, mb);
    fs.writeFileSync(path.join(out, kind + ".bin"), bytes);
    console.log(kind, bytes.length);
  }
}
