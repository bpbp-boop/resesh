const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const onboarding = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Controls", "OnboardingView.xaml.cs"),
  "utf8");
const previewXaml = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Dialogs", "ImportPreviewDialog.xaml"),
  "utf8");
const previewCode = fs.readFileSync(
  path.join(__dirname, "..", "src", "App", "Dialogs", "ImportPreviewDialog.xaml.cs"),
  "utf8");

test("Welcome import buttons open a preview before importing", () => {
  for (const source of ["Putty", "OpenSsh", "SecureCrt"])
    assert.match(
      onboarding,
      new RegExp(`private async void ${source}Import_Click[\\s\\S]*?await PreviewAndCommitImportAsync`));

  assert.match(
    onboarding,
    /var preview = new ImportPreviewDialog\(scan, source\)[\s\S]*?XamlRoot = XamlRoot/);
  assert.match(onboarding, /await preview\.ShowAsync\(\)/);
});

test("Welcome commits only the candidates confirmed in the preview", () => {
  assert.match(
    onboarding,
    /preview\.Confirmed is not \{ Count: > 0 \} confirmed[\s\S]*?return;[\s\S]*?SecureCrtImporter\.Commit\(App\.Store, confirmed\)/);
  assert.doesNotMatch(
    onboarding,
    /SecureCrtImporter\.Commit\(App\.Store, scan\.Importable\)/);
});

test("the preview lists aliases, targets, and destination folders with explicit confirmation", () => {
  assert.match(previewXaml, /PrimaryButtonText="Import"/);
  assert.match(previewXaml, /CloseButtonText="Cancel"/);
  assert.match(previewXaml, /Text="\{x:Bind Name\}"/);
  assert.match(previewXaml, /Text="\{x:Bind Detail\}"/);
  assert.match(previewXaml, /Text="\{x:Bind FolderDetail\}"/);
  assert.match(previewCode, /public string Name => Candidate\.Name/);
  assert.match(previewCode, /Candidate\.FolderPath\.Length > 0 \? \$"Folder: \{Candidate\.FolderPath\}" : "Folder: \(root\)"/);
});
