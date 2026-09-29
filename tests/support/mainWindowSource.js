const fs = require("node:fs");
const path = require("node:path");

// MainWindow is a partial class split across MainWindow*.cs; source checks read all of it
// so they don't depend on which file a member lives in.
const appDirectory = path.join(__dirname, "..", "..", "src", "App");

module.exports = fs.readdirSync(appDirectory)
  .filter(name => /^MainWindow\..*cs$/.test(name))
  .sort((a, b) => (a === "MainWindow.xaml.cs" ? -1 : b === "MainWindow.xaml.cs" ? 1 : a.localeCompare(b)))
  .map(name => fs.readFileSync(path.join(appDirectory, name), "utf8"))
  .join("\n");
