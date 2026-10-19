const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const appDirectory = path.join(__dirname, "..", "src", "App");

function xamlFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory())
      return entry.name === "bin" || entry.name === "obj" ? [] : xamlFiles(full);
    return entry.name.endsWith(".xaml") ? [full] : [];
  });
}

// Colors are defined once, as keyed brushes (App.xaml, theme dictionaries, or local
// resources that code-behind recolors), and controls refer to those brushes by name.
// Transparent is not a color choice and stays allowed.
const literalColor = /(?:#[0-9A-Fa-f]{3,8}|(?!Transparent")[A-Z][A-Za-z]+)"/;
const propertyLiteral = new RegExp(
  String.raw`\b(?:Background|Foreground|Fill|Stroke|BorderBrush)="` + literalColor.source);
const unkeyedBrushLiteral = new RegExp(
  String.raw`<SolidColorBrush(?![^>]*x:Key)[^>]*\bColor="` + literalColor.source);

test("XAML controls use named brushes instead of literal colors", () => {
  const offenders = [];
  for (const file of xamlFiles(appDirectory)) {
    const lines = fs.readFileSync(file, "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
      if (propertyLiteral.test(line) || unkeyedBrushLiteral.test(line))
        offenders.push(`${path.relative(appDirectory, file)}:${index + 1}: ${line.trim()}`);
    });
  }
  assert.deepEqual(offenders, []);
});
