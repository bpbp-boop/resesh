# Optional shell integration

Study date: 2026-09-10. Scope: local shells and SSH, including persistent sessions.
Status: implemented as an experimental per-session option, off by default.

## Implemented behavior and validation

- Local profile editors expose an enable toggle. SSH editors expose Off, Bash, Zsh,
  Fish, and PowerShell (Windows); there is no automatic remote platform selection.
  Existing profiles and imports remain disabled. The preference survives save/reload,
  cloning, and backup round trips.
- Local launch plans merge the user's environment first, retain intentionally empty
  generated values, and validate interactive arguments. Unsupported executables,
  WSL, and custom command/script invocations start unchanged with a skip notice.
- Bundled, attributed scripts are embedded in Core and staged in a versioned local
  cache. SSH uses a separate exec channel with a ten-second deadline and bounded
  captured output, followed by PTY+exec for interactive startup. No setup is typed
  into the interactive terminal. No profile files are edited or remote code downloaded.
- SSH.NET 2026.0.0 lacks a public PTY+exec ShellStream API. The implementation uses
  one narrow reflection adapter, pinned by compatibility tests and a real loopback
  SSH request test, alongside the existing resize adapter. If the adapter is unavailable,
  integration is skipped before staging. This supersedes the study's preferred patch below.
- New tmux shells receive hooks with DCS passthrough enabled on the private resesh
  server. Existing shells are only attached. Failed tmux startup retains successfully
  staged integration in a normal shell; failed staging uses the executable account
  shell or `sh`. Windows PowerShell plus persistence is rejected
  by the editor. A changed preference takes effect only when creating a new shell.
- OSC 9;9 Windows folders have a bounded, validated channel in both terminal surfaces
  and never become agent notifications. OSC 133 must be valid before it takes
  precedence over prompt discovery. Setup failures have short terminal notices;
  there is no separate activation-status badge in this first implementation.
- Bash handles blank Enter, scalar/array prompt hooks, and prompts rebuilt by those
  hooks. PowerShell retains the status seen by the original prompt, distinguishes a
  provable native exit result from later cmdlet failure, and ignores non-filesystem
  providers. It respects effective execution policy, including a policy that blocks
  the integration script. Blocked loads show a concise notice and leave a usable shell,
  without printing the raw exception. Compound PowerShell failures conservatively report 1.

Validation includes the Core, AppLogic, Terminal, and JavaScript suites; an x64 app
build; actual Git Bash and Windows PowerShell 5.1 ConPTY launches; and a loopback
SSH server observing PTY, exec, input, resize, and disconnect. The loopback verifies
that disabled connections make zero integration exec requests. A real Git Bash
harness executes the remote setup and launch with spaces and apostrophes in HOME,
and tests tmux fallback and attach paths with a controlled tmux substitute.
The isolated app demo verified Off defaults, SSH Bash and local enable persistence,
independence of another session's setting, and the PowerShell/persistence validation.
Git Bash OSC 7 folders map through `cygpath` to Windows paths, including mounted roots
and names containing spaces, apostrophes, percent signs, and `#`.
SSH setup diagnostics are queued onto the UI thread and discarded after cancellation;
the connection worker never calls WebView2's notice API directly.
POSIX setup scripts always use Unix line endings, including when generated on Windows.
Strict `dash` execution covers this requirement and setup failures include bounded,
sanitized diagnostics. Live Linux SSH tests verify Bash success and failure markers;
a separate Linux host running tmux 3.3a verifies passthrough in a new pane and after
reconnecting to that same pane without staging again. These tests used a disposable
tmux server, which was removed afterward.
The same host's captured output is replayed through xterm to verify command labels
and exit colours, including tmux forwarding B before drawing the prompt. When that
leaves the input boundary at column zero, recognizable user/host and PowerShell
prompts are removed after collecting soft wraps. Exact nonzero boundaries retain
their normal behavior. Idle tmux tabs use validated OSC 7 directories even when
tmux's title remains the shell name; running commands and program titles still win.
Exact command starts carry their lifecycle provenance through both terminal bridges.
While an exact command runs, delayed shell titles and prompt-shaped redraws cannot
end it; tmux interpreter titles do not replace the captured command name. Completion
retires the old process title and restores the current-directory label.

**Coverage limits:** native Zsh/Fish, PowerShell 7, and a Windows OpenSSH server
have not been exercised end to end.
WSL has no adapter yet. Arbitrary prompt plugins and nested-shell configurations may
need additional compatibility work, including custom tmux prompts that cannot be
recognized when passthrough markers precede drawing. Remote installations remain cached so live tmux
shells never lose their scripts. Existing terminal-provided OSC continues to work
when this option is off.

The rest of this document preserves the original upstream study and design rationale;
its proposed follow-up work is not a claim of implemented or tested behavior.

## Original recommendation

Add **Enable shell integration (experimental)** to each local profile and SSH session,
off by default. Reuse the existing OSC 133 command-mark and OSC 7 directory consumers.
SSH additionally requires an explicit remote shell/platform selection; do not discover
whether an unknown endpoint is a Unix host by executing Unix commands on it.
Bundle a pinned, reviewed set of shell scripts and adapt their startup mechanisms.
The referenced library is a useful starting point, but cannot be connected unchanged
to either our local environment builder or our SSH transport.

The option applies to newly started shells. Turning it off does not remove hooks from
an already running shell. A resumed tmux shell keeps the integration it was created with.
Existing shell-provided OSC reports continue to work when injection is off.

This deliberately changes the old roadmap's manual-only policy: setup becomes automatic
only for sessions where the user has enabled this option. Default SSH connections,
including network equipment, still receive no integration setup.

## What the referenced project does

Reviewed [confetty-sh/terminal-shell-integration at
8e0c8d9](https://github.com/confetty-sh/terminal-shell-integration/tree/8e0c8d9c33a99fd7166ed02be94134890843b79b).
Its project targets .NET 10, declares MIT licensing, and has no external runtime dependencies.
The [C# API](https://github.com/confetty-sh/terminal-shell-integration/blob/8e0c8d9c33a99fd7166ed02be94134890843b79b/src/Terminal.ShellIntegration/ShellIntegration.cs)
accepts a shell path, argument list, environment, and script directory. `Prepare` returns
replacement arguments/environment, shell kind, and an injected/skipped result. The host
still owns spawning, transport, packaging, and the user's enable/disable choice.

| Shell | Startup mechanism | Hooks |
| --- | --- | --- |
| Bash | Prepend `--posix`; point `ENV` at a script that exits POSIX mode and replays startup files | `PROMPT_COMMAND`, `PS0`, `PS1` |
| Zsh | Redirect `ZDOTDIR` to forwarding startup files | `precmd`, `preexec`, prompt suffix |
| Fish | Add the script tree to `XDG_DATA_DIRS` so `fish/vendor_conf.d` is sourced | Fish prompt/preexec/postexec events and prompt wrapper |
| PowerShell | Append `-NoExit -Command` to source the script after profiles | Wrap `prompt` and `PSConsoleHostReadLine` |

The scripts report prompt start/end (`133;A/B`), command start (`133;C`), completion
(`133;D;status`), and directory changes. Unix paths use OSC 7; Windows PowerShell uses
OSC 9;9. The library skips unknown executables, recognized command/file invocations,
its own inherited integration marker, and missing resource directories. These checks
are startup decisions, not proof that hooks actually loaded. See the pinned
[scripts](https://github.com/confetty-sh/terminal-shell-integration/tree/8e0c8d9c33a99fd7166ed02be94134890843b79b/src/Terminal.ShellIntegration/shell-integration)
and [README](https://github.com/confetty-sh/terminal-shell-integration/blob/8e0c8d9c33a99fd7166ed02be94134890843b79b/README.md).

## What already fits Resesh

- `src/Terminal/wwwroot/addon-ruler.js` consumes OSC 133 for command text, marks,
  completion status, and the command panel. Once OSC 133 is seen, prompt discovery defers.
- `TerminalControl` and `NativeTerminalSurface` expose directory/context reports;
  `TerminalTabView` validates OSC 7 and OSC 3008 before updating directory tracking.
- `LocalTerminalSession.Start` controls the exact executable, arguments, and child
  environment immediately before `CreateProcessW`.
- `SshTerminalSession.Connect` already has a bootstrap factory after authentication
  and before the interactive shell opens. Persistent sessions use it for tmux selection.

No new terminal renderer or private command protocol is needed for the basic feature.
OSC 3008 remains useful when a shell already supplies it; it is not something these
scripts currently emit.

## Results from the local probe

Used the downloaded, unchanged integration scripts and C# transform with Resesh's
existing `LocalTerminalSession` through a real ConPTY. Test homes and captures are
under `.artifacts/shell-integration-study/`; the reviewed upstream files are in the
temporary `resesh-shell-integration-study` directory. PowerShell ran without profiles;
PSReadLine was explicitly imported to disable history saving in the disposable probe.
This verifies the byte path, not arbitrary user prompt configurations or the full UI.

| Probe | Observed result |
| --- | --- |
| Git Bash, `--noprofile --norc -i` | A/B/C/D all arrived; successful command reported 0, `false` reported 1; OSC 7 arrived |
| Git Bash, ordinary `-i` through the current environment builder | `Prepare` said injected, but no integration marks arrived |
| Git Bash, user `PROMPT_COMMAND=('true')` | A/B/C/D and statuses 0 then 1 arrived |
| Git Bash, empty Enter after `false` | Another `D;1` arrived without a C; current ruler logic can create an empty-command mark |
| Windows PowerShell 5.1, current execution policy | Script load rejected; ordinary prompt remained, no integration marks |
| Windows PowerShell 5.1, process-only `RemoteSigned` | A/B/C/D and OSC 9;9 arrived; no saved execution policy changed |
| PowerShell native failure followed by cmdlet failure | `cmd /d /c exit 7` reported 7; subsequent `Write-Error` also reported stale 7 |
| Existing Resesh `AgentOsc.Parse(9, directoryPayload)` | Classified the directory report as a notification |

Also ran the existing `RulerCommandTitleTests`, `RulerCommandsPanelTests`,
`Osc7IntegrationTests`, and `Osc3008IntegrationTests`: **47 passed**.
No live SSH, tmux, WSL, Zsh, Fish, or PowerShell 7 session was tested in this study.

## Changes required before enabling it

1. **Build a final environment, then inject.** Apply inherited values and user overrides
   first, with our existing empty-value-means-remove semantics. Apply integration changes
   afterwards and serialize the final environment verbatim, including intentionally empty
   values. The upstream Bash script checks whether `TERMINAL_SHELL_BASH_INJECT` exists;
   for plain `-i` its value is empty. Our current builder deletes it, silently disabling
   the script. Do not re-expand `%NAME%` in generated arguments or environment values.

2. **Handle Windows directories separately from notifications.** Recognize and validate
   OSC 9;9 before the generic OSC 9 notification path in both terminal surfaces. Use a
   typed Windows-directory signal rather than passing a drive path to the Unix OSC 7
   parser. Otherwise each PowerShell prompt can cause a false attention signal. Preserve
   the existing process-directory fallback. Git Bash `/c/...` paths also need explicit
   local path mapping; a Unix path is not automatically a usable Windows path.

3. **Fix completion and prompt edge cases in the scripts.** PowerShell must distinguish
   a current native exit status from a stale `$LASTEXITCODE`. Bash must distinguish an
   executed command from an empty Enter. Exercise prompt frameworks that replace `PS1`
   or prompt functions after startup, existing integration, shell nesting, Ctrl+C,
   multiline input, and preservation of the status seen by the user's original prompt.

4. **Validate the actual scripts and launch forms.** The library checks directory
   existence rather than every required file. Its PowerShell option classifier lists
   selected spellings, so it needs coverage for accepted abbreviations, script paths,
   and custom command arguments before altering a profile. `wsl.exe` and `cmd.exe` are
   unsupported by `Identify`; WSL requires its own adapter. Test Git Bash login startup,
   paths with spaces/quotes/percent signs, and resource deployment in MSI/portable builds.

5. **Report observed state.** Keep `Disabled`, `Starting`, `Active`, and `Unavailable`
   with a short reason. `Prepare.Injected` only means a launch was prepared. Confirm
   activation from the stream and retain discovery when setup fails. Review the ruler's
   permanent switch on the first OSC 133 message: malformed or incomplete integrations
   must not silently eliminate useful command discovery. Respect effective PowerShell
   execution policy and report rejection; do not silently change it or bypass it.

## Local launch design

Put the preference on `Session`, alongside other connection behavior, rather than in
appearance-only `TerminalOverrides`. A simple `ShellIntegrationMode` enum with
`Disabled = 0` and `Automatic = 1` keeps existing saved profiles disabled on deserialization.
Use the same preference in `LocalProfileEditDialog` and `SessionEditDialog`.
`Automatic` identifies a known local executable only. For SSH, require a separate
explicit shell/platform choice before enabling setup; there is no probe-every-host mode.

Introduce a core `ShellIntegrationLaunchPlan` containing executable, arguments, final
environment, selected shell, and outcome. `LocalTerminalSession.Start` obtains the plan
after expanding user configuration and before building the native process structures.
The original saved `LocalTarget` remains reusable; transient launch values are not saved.

For recognized native shells, use the mechanisms above with bundled script paths.
For WSL, preserve distribution/user/working-directory selection, resolve the Linux shell
inside that distribution, and use Linux-visible script paths and environment values.
Do not feed `wsl.exe` to the library and treat its skip result as WSL support. Command
Prompt and unsupported/custom invocations continue normally with a clear unavailable reason.

## SSH launch design

The library assumes the caller is on the machine spawning the shell: it uses local
filesystem checks, `Path.Combine`, and `Path.PathSeparator`. Calling `Prepare` on Windows
with `/home/user/...` does not produce a valid remote launch plan. Reuse the scripts and
mechanisms, with a separate remote path/argument builder.

### Network CLI boundary

An SSH exec channel is a request to execute a command, not a capability-discovery API.
It does not guarantee a POSIX shell. A timeout/output limit only bounds the client's
wait and capture; it does not prevent command authorization, accounting, or CLI errors.
Cisco documents [EXEC command authorization](https://www.cisco.com/c/en/us/td/docs/switches/lan/catalyst9300/software/release/17-12/configuration_guide/sec/b_1712_sec_9300_cg/configuring_authorization.html),
and Junos treats [Unix shell access](https://www.juniper.net/documentation/us/en/software/junos/cli-reference/topics/ref/command/show-cli-authorization.html)
as a distinct permission. An underlying Unix OS does not make a network CLI a supported
shell-integration target.

Require the user to select the remote shell and platform when enabling SSH integration.
For example, Bash/Zsh/Fish on Unix or PowerShell on Windows. Unknown and network-device
sessions receive no integration probes, uploads, or bootstrap commands. Never issue
`start shell`, `run bash`, `enable`, or similar commands to reach a different environment.
Passive banner/prompt evidence can suggest that integration is unavailable, but cannot
authorize probing; an OpenSSH banner alone is insufficient. Existing passive prompt
discovery and any OSC the endpoint already emits remain available.

For an enabled SSH session with an explicitly selected supported shell/platform:

1. After authentication, use a bounded, shell-specific preflight to validate the selected
   shell/version and obtain its account home and staging availability. This validates an
   explicit target; it does not discover the platform of an unknown endpoint. On a mismatch,
   rejection, or timeout, stop integration setup and report it unavailable. Do not try
   other shell syntaxes or retry through interactive input. Ordinary login can continue
   if the transport remains usable; do not assume a rejected request leaves it usable.
2. Stage a versioned set of bundled scripts in a private directory belonging to that account,
   such as `~/.cache/resesh/shell-integration/<content-hash>/`. Use restrictive ownership/
   permissions, reject symlink substitutions, upload atomically, and quote paths as data.
   No remote downloads and no edits to `.bashrc`, `.zshrc`, or PowerShell profiles.
   Windows SSH needs a Windows staging/launch adapter; POSIX setup must not be sent to it.
3. Start the interactive shell with the selected startup wrapper and original terminal size.
   Keep user input gated until startup is ready. Failure must leave one usable shell, with
   no delayed retry that could execute setup inside a foreground application.

**Transport choice:** prefer a PTY plus SSH `exec` request for the wrapper, so there is
one intentional shell startup and setup never competes with the interactive input stream.
SSH.NET's current public `ShellStream` path sends a PTY request followed immediately by
a shell request; it does not accept a replacement startup command. Its
[implementation](https://github.com/sshnet/SSH.NET/blob/develop/src/Renci.SshNet/ShellStream.cs)
confirms this. Implementing PTY+exec therefore needs a supported transport extension or
a narrowly maintained SSH.NET patch; avoid adding another dependency on private reflection.

The existing `bootstrapCommand` is a cheaper prototype path: launch the normal shell,
then execute a prepared wrapper as its first input. It is not equivalent to spawn-time
injection. It may read startup files twice, leave setup in history/recordings, or meet a
program started by the user's login files. If tried, confine it to opted-in, verified
shell sessions, with explicit startup-state handling; do not blindly reuse it for every host.

## Persistent tmux sessions

Inject into the shell created by `tmux new-session`, not into the outer shell that is
immediately replaced by tmux. Give each new session its own wrapper/environment instead
of changing global defaults for every session on the private `resesh-app` server.

Resume/attach must never send setup to the existing pane: it may be running an editor,
agent, password prompt, or long job. Show that a non-integrated existing shell needs a
new persistent session; an integrated shell retains its hooks across disconnects.
Keep its referenced script version available for the shell's lifetime.

tmux is another parser, not a transparent pipe. Its
[OSC handlers](https://github.com/tmux/tmux/blob/master/input.c)
store directory and command metadata internally. Verify which sequences reach Resesh
on the supported tmux versions; ordinary OSC emission is not sufficient proof.
A likely adapter uses tmux DCS passthrough for our reports with `allow-passthrough` enabled
only on our private server. Verify cursor positions and event ordering as well as bytes.
This is a proposed approach, not a tested result. Existing `capture-pane -e` replay must
not be assumed to reconstruct historical command boundaries or exit results.

## Suggested implementation order

1. Shared setting/status model, final-environment refactor, OSC 9;9 routing, and regression
   cases for the reproduced failures. Pin the scripts and retain their license notice.
2. Local PowerShell and Git Bash integration with real ConPTY tests, then WSL adaptation.
3. Remote staging and PTY+exec transport spike against a disposable OpenSSH server; verify
   both ordinary remote sessions and failure fallback before exposing SSH injection.
   Assert that disabled, unknown, and network-device targets produce zero integration
   exec requests, uploads, and bootstrap writes.
4. New tmux-session startup and passthrough, followed by reconnect while idle and while a
   foreground program is running. No setup on resume.
5. Broaden shell/profile/version coverage and verify the setting and status in the app,
   including command marks, copied output boundaries, directory following, and recording.

This gives one option with consistent intent across local and SSH profiles, while keeping
the three distinct startup paths explicit in the implementation.
