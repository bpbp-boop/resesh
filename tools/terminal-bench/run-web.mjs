// Feeds the shared workload files through the real terminal page (xterm.js + WebGL + resesh
// addons) in headless Edge, in the 32 KiB base64 batches TerminalControl.FlushOutput posts.
// --paced replays a prefix of each workload at --rate=MB/s for --seconds=N and reports CPU time of
// every browser process (renderer, GPU, browser) instead of throughput.
// usage: node run-web.mjs <page.html> <workloadDir> <label> [workloads,comma] [--reps=N] [--profile]
//        [--paced] [--rate=2] [--seconds=3]
import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const positional = process.argv.slice(2).filter(a => !a.startsWith("--"));
const rest = process.argv.slice(2).filter(a => a.startsWith("--"));
const [pagePath, wlDir, label = "web", wlArg = "logs,color,tiny,tui,unicode"] = positional;
const doProfile = rest.includes("--profile");
const paced = rest.includes("--paced");
const rateMBps = Number((rest.find(a => a.startsWith("--rate=")) || "--rate=2").slice(7));
const pacedSeconds = Number((rest.find(a => a.startsWith("--seconds=")) || "--seconds=3").slice(10));
const reps = Number((rest.find(a => a.startsWith("--reps=")) || "--reps=3").slice(7));
const port = 9300 + Math.floor(Math.random() * 500);
const outDir = path.dirname(path.resolve(pagePath));
const udd = path.join(outDir, "udd-" + port);
const edge = spawn("C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", [
  "--headless=new", `--remote-debugging-port=${port}`, `--user-data-dir=${udd}`, "--window-size=1400,900",
  "--no-first-run", "--disable-extensions", "--allow-file-access-from-files", "--enable-gpu", "--ignore-gpu-blocklist", "--disable-component-update", "--disable-background-networking",
  "--disable-features=msEdgeCollections,msEntityExtraction", ...(process.env.EXTRA_EDGE_ARGS ? process.env.EXTRA_EDGE_ARGS.split(" ") : []),
  "about:blank"], { stdio: "ignore" });

async function targets() {
  for (let i = 0; i < 100; i++) {
    try { return await (await fetch(`http://127.0.0.1:${port}/json`)).json(); } catch { await new Promise(r => setTimeout(r, 100)); }
  }
  throw new Error("no cdp");
}
const page = (await targets()).find(t => t.type === "page");
const browserWsUrl = (await (await fetch(`http://127.0.0.1:${port}/json/version`)).json()).webSocketDebuggerUrl;
const bws = new WebSocket(browserWsUrl);
await new Promise(r => bws.addEventListener("open", r));
let bid = 0; const bpending = new Map();
bws.addEventListener("message", e => { const m = JSON.parse(e.data); if (m.id && bpending.has(m.id)) { bpending.get(m.id)(m); bpending.delete(m.id); } });
const bsend = (method, params = {}) => new Promise(r => { const i = ++bid; bpending.set(i, r); bws.send(JSON.stringify({ id: i, method, params })); });
// cpuTime (seconds) summed per process type
async function cpuByType() {
  const r = await bsend("SystemInfo.getProcessInfo");
  const out = {};
  for (const p of r.result.processInfo) out[p.type] = (out[p.type] || 0) + p.cpuTime;
  return out;
}
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise(r => ws.addEventListener("open", r));
let id = 0; const pending = new Map();
ws.addEventListener("message", e => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } });
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });
const evaluate = async (expr) => {
  const r = await send("Runtime.evaluate", { expression: expr, awaitPromise: true, returnByValue: true });
  if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails).slice(0, 800));
  return r.result?.result?.value;
};

await send("Network.setCacheDisabled", { cacheDisabled: true });
await send("Page.enable");
await send("Page.navigate", { url: pathToFileURL(path.resolve(pagePath)).href });
for (let i = 0; i < 100 && !(await evaluate("!!window.__ready")); i++) await new Promise(r => setTimeout(r, 100));
await evaluate(`window.__send({type:'connected', executionGeneration:1}); true`);
// The real page fits to the window; headless Edge can report a small window until its first
// layout settles. Every result must come from the same 170x50 grid as the native side.
for (let i = 0; i < 50 && (await evaluate("window.__term.cols")) !== 170; i++) await new Promise(r => setTimeout(r, 100));
const grid = await evaluate("[window.__term.cols, window.__term.rows]");
if (grid[0] !== 170 || grid[1] !== 50) throw new Error("grid is " + grid.join("x") + ", expected 170x50");
const env = await evaluate(`(() => {
  const c = document.createElement('canvas').getContext('webgl2');
  const ext = c && c.getExtension('WEBGL_debug_renderer_info');
  return { cols: window.__term.cols, rows: window.__term.rows, dpr: devicePixelRatio,
    renderer: document.querySelector('.xterm canvas') ? 'webgl' : 'dom',
    gpu: ext ? c.getParameter(ext.UNMASKED_RENDERER_WEBGL) : 'unknown' };
})()`);
console.log(label, "env", JSON.stringify(env));

await evaluate(`
window.__runFeed = function (msgs) {
  return new Promise(resolve => {
    const t0 = performance.now(); let k = 0; let longest = 0; let last = performance.now();
    const ch = new MessageChannel();
    // longest main-thread gap while feeding (input latency proxy)
    const probe = setInterval(() => { const now = performance.now(); longest = Math.max(longest, now - last); last = now; }, 5);
    ch.port1.onmessage = () => {
      if (k < msgs.length) { window.__send(msgs[k++]); ch.port2.postMessage(0); return; }
      const fed = performance.now() - t0;
      window.__term.write('', () => requestAnimationFrame(() => requestAnimationFrame(() => {
        clearInterval(probe);
        resolve({ fedMs: +fed.toFixed(1), totalMs: +(performance.now() - t0).toFixed(1), longestGapMs: Math.round(longest) });
      })));
    };
    ch.port2.postMessage(0);
  });
};
window.__runPaced = function (msgs, intervalMs) {
  return new Promise(resolve => {
    const t0 = performance.now(); let k = 0; let longest = 0; let last = performance.now();
    const probe = setInterval(() => { const now = performance.now(); longest = Math.max(longest, now - last); last = now; }, 5);
    function tick() {
      const now = performance.now();
      while (k < msgs.length && t0 + k * intervalMs <= now) window.__send(msgs[k++]);
      if (k < msgs.length) { setTimeout(tick, Math.max(0, t0 + k * intervalMs - performance.now())); return; }
      window.__term.write('', () => requestAnimationFrame(() => requestAnimationFrame(() => {
        clearInterval(probe);
        resolve({ totalMs: +(performance.now() - t0).toFixed(1), longestGapMs: Math.round(longest) });
      })));
    }
    tick();
  });
};
true`);

const results = { env };
for (const wl of wlArg.split(",")) {
  let bytes = fs.readFileSync(path.join(wlDir, wl + ".bin"));
  if (paced) bytes = bytes.subarray(0, Math.min(bytes.length, Math.floor(rateMBps * pacedSeconds * 1048576)));
  // Ship the base64 batches into the page once, outside the timed region.
  const batches = [];
  for (let o = 0; o < bytes.length; o += 32768) batches.push(bytes.subarray(o, Math.min(bytes.length, o + 32768)).toString("base64"));
  await evaluate(`window.__b64 = []; true`);
  for (let i = 0; i < batches.length; i += 64)
    await evaluate(`window.__b64.push(...${JSON.stringify(batches.slice(i, i + 64))}); true`);
  const times = [];
  for (let r = 0; r < reps; r++) {
    await evaluate(`window.__msgs = window.__b64.map(d => ({ type: 'output', data: d, ingest: [{ offset: 0, unixMs: Date.now() }] })); window.__term.reset(); true`);
    const profiling = doProfile && r === reps - 1;
    if (profiling) { await send("Profiler.enable"); await send("Profiler.setSamplingInterval", { interval: 100 }); await send("Profiler.start"); }
    let res;
    if (paced) {
      await send("Performance.enable");
      const m0 = Object.fromEntries((await send("Performance.getMetrics")).result.metrics.map(m => [m.name, m.value]));
      const c0 = await cpuByType();
      res = await evaluate(`window.__runPaced(window.__msgs, ${32768 / (rateMBps * 1048576) * 1000})`);
      const c1 = await cpuByType();
      const m1 = Object.fromEntries((await send("Performance.getMetrics")).result.metrics.map(m => [m.name, m.value]));
      res.cpuMs = Object.fromEntries(Object.keys(c1).map(k => [k, Math.round(((c1[k] || 0) - (c0[k] || 0)) * 1000)]));
      res.cpuMs.total = Object.values(res.cpuMs).reduce((a, b) => a + b, 0);
      // Renderer + GPU is the like-for-like figure; the browser process also carries headless
      // Edge's own background services.
      res.cpuMs.rendererGpu = (res.cpuMs.renderer || 0) + (res.cpuMs.GPU || 0);
      res.mainThreadTaskMs = Math.round((m1.TaskDuration - m0.TaskDuration) * 1000);
      res.cpuPerSec = +(res.cpuMs.total / (res.totalMs / 1000)).toFixed(1);
    } else {
      res = await evaluate(`window.__runFeed(window.__msgs)`);
    }
    if (profiling) {
      const { result } = await send("Profiler.stop");
      fs.writeFileSync(path.join(outDir, `${label}-${wl}.cpuprofile`), JSON.stringify(result.profile));
    }
    if (rest.includes("--snapshot") && !paced) {
      // Rewind keyframe cost: what captureKeyframe does (serialize + base64), on the full buffer.
      Object.assign(res, await evaluate(`(() => {
        const s = new SerializeAddon.SerializeAddon(); window.__term.loadAddon(s);
        const t0 = performance.now(); const text = s.serialize(); const t1 = performance.now();
        const b64 = new TextEncoder().encode(text).toBase64(); const t2 = performance.now();
        s.dispose();
        return { serializeMs: +(t1 - t0).toFixed(1), base64Ms: +(t2 - t1).toFixed(1), keyframeKB: Math.round(b64.length / 1024) };
      })()`));
    }
    times.push(res);
  }
  // Paced wall time is fixed by the schedule; report the median-CPU run instead.
  const best = paced
    ? [...times].sort((a, b) => a.cpuMs.rendererGpu - b.cpuMs.rendererGpu)[Math.floor(times.length / 2)]
    : times.reduce((a, b) => (a.totalMs < b.totalMs ? a : b));
  results[wl] = { bytes: bytes.length, ...best, mbps: +(bytes.length / 1048576 / (best.totalMs / 1000)).toFixed(2), all: times.map(t => t.totalMs) };
  console.log(label, wl, JSON.stringify(results[wl]));
}
fs.writeFileSync(path.join(outDir, `${label}-results.json`), JSON.stringify(results, null, 1));
ws.close(); bws.close(); edge.kill();
setTimeout(() => { try { fs.rmSync(udd, { recursive: true, force: true }); } catch {} process.exit(0); }, 500);
