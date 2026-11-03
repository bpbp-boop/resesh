// Build and identity facts that live in more than one file because the files are different
// languages: the workflow, the projects, the installer and the app. Each check fails when
// one copy changes without the others.
const assert = require("node:assert/strict");
const { spawnSync } = require("node:child_process");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");

const root = path.join(__dirname, "..");
const read = file => fs.readFileSync(path.join(root, file), "utf8").replace(/\r\n/g, "\n");

const workflow = read(".github/workflows/build-release.yml");
const terminalProject = read("src/Terminal/Resesh.Terminal.csproj");
const appProject = read("src/App/Resesh.App.csproj");
const sdkProps = read("eng/WindowsAppSdk.props");
const packageWxs = read("installer/Package.wxs");
const bundleWxs = read("installer/Bundle.wxs");
const buildInfo = read("src/App/AppBuildInfo.cs");
const taskbar = read("src/App/Interop/TaskbarIntegration.cs");
const aboutXaml = read("src/App/Controls/AboutView.xaml");

/** The steps of one workflow job, by name, as raw YAML text. */
function jobSteps(job) {
  const start = workflow.indexOf(`\n  ${job}:\n`);
  assert.ok(start >= 0, `workflow has a ${job} job`);
  const next = workflow.slice(start + 1).search(/\n {2}[a-z-]+:\n/);
  const body = next < 0 ? workflow.slice(start) : workflow.slice(start, start + 1 + next);
  const steps = new Map();
  for (const chunk of body.split(/\n {6}- name: /).slice(1))
    steps.set(chunk.slice(0, chunk.indexOf("\n")).trim(), chunk);
  return steps;
}

function csharpConst(source, name) {
  const value = source.match(new RegExp(`const string ${name} = "([^"]+)"`))?.[1];
  assert.ok(value, `${name} is defined`);
  return value;
}

test("every libghostty-vt cache key hashes exactly the native build inputs", () => {
  const include = terminalProject.match(/<GhosttyVtInput Include="([^"]+)"/)?.[1];
  assert.ok(include, "Resesh.Terminal.csproj lists GhosttyVtInput");
  const inputs = include.split(";").map(item => item
    .replace("$(MSBuildThisFileDirectory)..\\..\\", "")
    .replace("$(MSBuildThisFileDirectory)", "src\\Terminal\\")
    .replaceAll("\\", "/")
    .replace(/\/\*\*\/\*$/, "/**"));

  const keys = [...workflow.matchAll(/key: ghostty-vt-.*hashFiles\(([^)]*)\)/g)].map(match =>
    [...match[1].matchAll(/'([^']+)'/g)].map(file => file[1]));
  assert.equal(keys.length, 2, "the test and build jobs both cache libghostty-vt");
  for (const key of keys)
    assert.deepEqual([...key].sort(), [...inputs].sort());
});

test("both jobs take the build version from one script", () => {
  for (const job of ["build", "sign"]) {
    const step = jobSteps(job).get("Set build version");
    assert.ok(step, `${job} sets the build version`);
    assert.match(step, /\$version = \.\/eng\/build-version\.ps1\n/);
    assert.doesNotMatch(step, /GITHUB_REF_NAME/);
  }
});

test("the version script maps tags and runs to numeric MSI versions", { skip: process.platform !== "win32" }, () => {
  const shell = spawnSync("pwsh", ["-NoProfile", "-Command", "exit 0"]).status === 0 ? "pwsh" : "powershell";
  const run = (...args) => spawnSync(shell,
    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path.join(root, "eng", "build-version.ps1"), ...args],
    { encoding: "utf8", env: { ...process.env, GITHUB_REF_TYPE: "", GITHUB_REF_NAME: "", GITHUB_RUN_NUMBER: "" } });

  const tag = run("-RefType", "tag", "-RefName", "v1.2.3");
  assert.equal(tag.status, 0, tag.stderr);
  assert.equal(tag.stdout.trim(), "1.2.3");
  const branch = run("-RefType", "branch", "-RefName", "master", "-RunNumber", "42");
  assert.equal(branch.stdout.trim(), "0.0.42");
  assert.notEqual(run("-RefType", "tag", "-RefName", "v1.2").status, 0);
  assert.notEqual(run("-RefType", "tag", "-RefName", "v1.2.3-beta").status, 0);
});

test("unsigned and signed builds package the MSI and bundle the same way", () => {
  // The sign job repeats the build job's packaging with signing in between. Signing is the
  // only difference allowed: the same projects, properties, asset names and prerequisites.
  const unsigned = jobSteps("build").get("Create MSI, setup bundle, and portable ZIP");
  const signed = jobSteps("sign").get("Create and sign release assets");
  assert.ok(unsigned && signed);

  const packaging = step => ({
    // The command, its backtick-continued lines and the last one.
    builds: [...step.matchAll(/dotnet build (installer\/\S+)((?:[^\n]*`\n)+[^\n]*)/g)].map(match => [
      match[1],
      [...match[2].matchAll(/(-p:\w+="?[^`\n"]*"?|--configuration \w+)/g)]
        .map(arg => arg[1].trim())
        .filter(arg => arg !== "-p:SignOutput=true")
        .sort(),
    ]),
    lines: step.split("\n").map(line => line.trim()).filter(line =>
      /^\$(outputDir|assetPrefix|zipPath|msiPath|setupPath) =/.test(line)
      || line.startsWith("Compress-Archive")
      || line.startsWith("./installer/download-prereqs.ps1")),
  });

  const expected = packaging(unsigned);
  assert.equal(expected.builds.length, 2, "the MSI and the bundle are built");
  assert.ok(expected.lines.length >= 7);
  assert.deepEqual(packaging(signed), expected);
});

test("the app and the terminal library restore the same Windows App SDK packages", () => {
  for (const project of [appProject, terminalProject]) {
    assert.match(project, /<Import Project="\.\.\\\.\.\\eng\\WindowsAppSdk\.props" \/>/);
    for (const [name, property] of [
      ["Microsoft.WindowsAppSDK.WinUI", "WindowsAppSdkWinUIVersion"],
      ["Microsoft.WindowsAppSDK.InteractiveExperiences", "WindowsAppSdkInteractiveExperiencesVersion"],
    ]) {
      assert.match(project, new RegExp(`Include="${name}" Version="\\$\\(${property}\\)"`));
      assert.match(sdkProps, new RegExp(`<${property}>\\d+\\.\\d+\\.\\d+</${property}>`));
    }
  }
  assert.match(appProject, /Include="Microsoft\.WindowsAppSDK\.Runtime" Version="\$\(WindowsAppSdkRuntimeVersion\)"/);
  assert.doesNotMatch(appProject, /<WindowsAppSdkRuntimeVersion>/);
});

test("the installer's Start menu shortcut groups with the running app on the taskbar", () => {
  const appId = csharpConst(taskbar, "AppUserModelId");
  assert.match(packageWxs, new RegExp(`<ShortcutProperty Key="System\\.AppUserModel\\.ID" Value="${appId.replaceAll(".", "\\.")}" />`));
});

test("the installer and About link to the addresses AppBuildInfo defines", () => {
  const website = csharpConst(buildInfo, "WebsiteUrl");
  const issues = csharpConst(buildInfo, "IssuesUrl");
  assert.equal(packageWxs.match(/Id="ARPURLINFOABOUT" Value="([^"]+)"/)?.[1], website);
  assert.equal(packageWxs.match(/Id="ARPHELPLINK" Value="([^"]+)"/)?.[1], issues);
  assert.equal(bundleWxs.match(/AboutUrl="([^"]+)"/)?.[1], website);

  // About sets every link from AppBuildInfo in code; none is typed into the page.
  assert.doesNotMatch(aboutXaml, /(Tag|Description)="https?:/);
  assert.doesNotMatch(aboutXaml, /github\.com|resesh\.net/);
  const about = read("src/App/Controls/AboutView.xaml.cs");
  for (const name of ["WebsiteUrl", "ReleasesUrl", "IssuesUrl", "SourceUrl", "LicenseUrl"])
    assert.match(about, new RegExp(`Tag = AppBuildInfo\\.${name};`));
});
