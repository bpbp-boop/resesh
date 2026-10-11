const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const read = (...parts) => fs.readFileSync(path.join(__dirname, "..", "src", "App", ...parts), "utf8");
const onboarding = read("Controls", "OnboardingView.xaml.cs");
const importXaml = read("Controls", "ImportView.xaml");
const importCode = read("Controls", "ImportView.xaml.cs");
const importWindow = read("MainWindow.Import.cs");
const tabGroups = read("MainWindow.TabGroups.cs");

test("Welcome import buttons open the review tab before importing", () => {
  for (const source of ["Putty", "OpenSsh", "SecureCrt"])
    assert.match(
      onboarding,
      new RegExp(`private async void ${source}Import_Click[\\s\\S]*?await PreviewAndCommitImportAsync`));

  assert.match(onboarding, /await ReviewImport\(scan, source\) is not \{ \} outcome[\s\S]*?return;/);
  assert.match(tabGroups, /view\.ReviewImport = \(scan, source\) => Host\(\)\.ReviewImportAsync\(scan, source\)/);
  assert.match(importWindow, /ReviewImportAsync\(scan, sourceName\)/);
  assert.doesNotMatch(onboarding, /SecureCrtImporter\.Commit/);
});

test("the review commits only the selected candidates, with the identity plan", () => {
  assert.match(
    importCode,
    /var confirmed = _candidates\.Where\(c => c\.IsSelected\)[\s\S]*?SecureCrtImporter\.Commit\(App\.Store, confirmed, App\.SshKeys, _plan, App\.Identities\)/);
  assert.doesNotMatch(importCode, /SecureCrtImporter\.Commit\(App\.Store, scan\.Importable/);
});

test("closing the review tab cancels the import", () => {
  assert.match(importCode, /public void Dispose\(\) => _completion\.TrySetResult\(null\)/);
});

test("the review lists names, targets, folders, and logins with an explicit import action", () => {
  assert.match(importXaml, /Text="\{x:Bind Name\}"/);
  assert.match(importXaml, /Text="\{x:Bind Endpoint\}"/);
  assert.match(importXaml, /<DataTemplate x:DataType="local:ImportFolderRow">/);
  assert.match(importXaml, /IsChecked="\{x:Bind AllSelected, Mode=TwoWay\}"/);
  assert.match(importXaml, /Text="\{x:Bind LoginDetail, Mode=OneWay\}"/);
  assert.match(importXaml, /x:Name="ImportButton"[\s\S]*?Click="Import_Click"/);
  assert.match(importCode, /visible\.Where\(c => FolderPaths\.IsSelfOrDescendant\(c\.FolderPath, folder\)\)/);
});
