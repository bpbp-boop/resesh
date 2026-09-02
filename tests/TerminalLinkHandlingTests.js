const fs = require("fs");
const path = require("path");

const root = path.resolve(__dirname, "..");
const page = fs.readFileSync(path.join(root, "src", "Terminal", "wwwroot", "terminal.html"), "utf8");
const host = fs.readFileSync(path.join(root, "src", "Terminal", "TerminalControl.cs"), "utf8");
const policy = fs.readFileSync(path.join(root, "src", "Terminal", "TerminalSurface.cs"), "utf8");

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

assert(
  /new WebLinksAddon\.WebLinksAddon\(function \(_event, uri\) \{\s*host\.postMessage\(\{ type: "openLink", uri: uri \}\);\s*\}\)/m.test(page),
  "terminal links should be delegated to the native host"
);
assert(host.includes('case "openLink":'), "native host should handle openLink messages");
assert(policy.includes("Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps"), "link handling should allow only HTTP(S)");
assert(policy.includes("UseShellExecute = true"), "link handling should use the Windows default browser");

console.log("Terminal link handling tests passed.");
