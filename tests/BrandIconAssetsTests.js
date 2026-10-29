const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const catalog = fs.readFileSync(path.join(root, "src", "Core", "Models", "SessionIcons.cs"), "utf8");
const treeIcons = path.join(root, "src", "App", "Assets", "SessionTreeIcons");

// new("mysql", "MySQL", "mysql.png", BrandGroup.Databases, "...")
const brands = [...catalog.matchAll(/new\("([^"]+)", "[^"]+", "([^"]+\.png)", BrandGroup\.\w+,/g)]
  .map(match => ({ key: match[1], file: match[2] }));

test("the brand list is the one the tree and workspace icons share", () => {
  assert.ok(brands.length >= 60, `expected the brand list, found ${brands.length}`);
  assert.equal(new Set(brands.map(brand => brand.key)).size, brands.length, "brand keys repeat");
});

test("every brand mark ships as a one-colour PNG with its source SVG", () => {
  for (const { key, file } of brands) {
    assert.ok(fs.existsSync(path.join(treeIcons, file)), `${key}: ${file} is missing`);
    const svg = file.replace(/\.png$/, ".svg");
    assert.ok(fs.existsSync(path.join(treeIcons, svg)), `${key}: ${svg} is missing`);
  }
  const project = fs.readFileSync(path.join(root, "src", "App", "Resesh.App.csproj"), "utf8");
  assert.match(project, /Assets\\SessionTreeIcons\\\*\.png/);
});
