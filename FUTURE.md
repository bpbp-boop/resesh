# resesh Future

Ideas that are worth preserving but are not committed roadmap work. When an item is
scheduled, move it to [ROADMAP.md](ROADMAP.md) and keep implementation decisions in
[DECISIONS.md](DECISIONS.md).

---

## 1. Password-manager-backed SSH keys

**Goal:** let a resesh session authenticate with an SSH key held by 1Password,
Bitwarden, KeePass/KeePassXC, Keeper, Proton Pass, or another compatible agent. The
private key and its passphrase must never enter resesh storage.

This extends the `AuthMethod.Agent` work already listed under **Connectivity** in the
roadmap. It is an SSH-agent integration, not a separate SDK integration for each
password manager.

### Integration boundary

Use the standard SSH-agent request/signing protocol ([RFC 9987](https://www.rfc-editor.org/rfc/rfc9987.html)).
On Windows, support these transports:

1. The system-wide OpenSSH named pipe, `\\.\pipe\openssh-ssh-agent`.
2. A user-configurable agent endpoint for managers that publish a separate socket.
3. Pageant's protocol, as already planned, for KeePass/KeeAgent and PuTTY users.

Expected provider paths:

- **1Password** — its Windows agent takes over the system-wide OpenSSH pipe. It offers
  dedicated SSH Key items, biometric approval, and optional SSH Bookmarks.
- **Bitwarden** — its desktop agent replaces the native Windows agent. It supports
  Ed25519 and RSA keys, but currently has no per-host agent key selection or `ssh-add`
  constraints.
- **Keeper** — its desktop agent uses the system-wide OpenSSH pipe. KeeperPAM can add
  managed tunnels and key rotation, but resesh only needs the agent boundary.
- **KeePassXC** — it loads database-backed keys into Windows OpenSSH agent or Pageant;
  KeePassXC does not provide a separate agent endpoint.
- **KeePass with KeeAgent** — use its OpenSSH-compatible or Pageant interface. Do not
  depend on KeePass URL overrides or plugin-specific entry fields.
- **Proton Pass** — its desktop app generates the endpoint setup command. Treat it as a
  configurable compatible endpoint until its Windows endpoint contract is documented.

Provider detection is useful for display and setup help, but it must not change the
wire behavior. Unknown RFC-compatible agents must work without a provider adapter.

### Session and key identity

resesh already owns the destination identity: session id, display name, username,
host, and port. Keep that separate from the client key:

- Identify an agent key by its public-key algorithm and SHA-256 public-key fingerprint.
- Store the selected fingerprint in the session. Never store a password-manager item id,
  vault path, private key, or key passphrase.
- Allow **automatic/try available keys** and **pin this fingerprint** modes. Pinned mode
  is the safe default after the user selects a key and avoids SSH server authentication-
  attempt limits.
- Permit one agent key to serve several sessions and one destination to accept more than
  one key. Do not encode the destination into the key identity.
- Show the agent key comment as advisory text only. Comments are not unique or stable.

`ssh://user@host:port` is useful launch and import metadata, and 1Password uses it for
bookmarks. The `ssh` URI scheme is only provisionally registered, so it must not become
the durable database key. A first implementation must not scrape password-manager vaults
or try to import their saved hosts.

### Authentication flow

1. Connect to the selected agent endpoint and list public identities.
2. Resolve the pinned fingerprint, or form an ordered list for automatic mode.
3. Complete SSH key exchange and normal server host-key verification.
4. Ask the agent to sign the SSH authentication data with the selected public key.
5. Let the password manager show its own unlock or approval prompt.
6. Report agent unavailable, vault locked, request denied, key missing, and server
   rejection as different errors.

The base agent signing request contains the public key, data to sign, and flags. It does
not contain a friendly hostname. resesh can show the destination in its own UI, but must
not claim that the password manager's approval is bound to that host.

OpenSSH defines `session-bind@openssh.com` and
`restrict-destination-v00@openssh.com` extensions that can cryptographically bind agent
use to a server host key and forwarding path. Evaluate them after basic agent auth works.
Password-manager support is not consistent or clearly documented, so bookmarks and
per-session fingerprint selection must not be described as destination constraints.

### Security boundaries

- Never export or copy a private key from a provider.
- Never silently fall back from the selected agent to a local private-key file.
- Keep agent forwarding off by default. It is a separate, explicit feature because a
  remote host can request signatures through a forwarded agent.
- Preserve provider approval prompts. Do not cache a successful signature as authority
  for later signatures.
- Treat access to an unlocked agent endpoint as sensitive. Local malware can request
  signatures even when it cannot extract the private key.
- Prefer per-person keys. Shared private keys in organization vaults weaken attribution
  and revocation.

### Acceptance

- A session can list, select, pin, and use a key through the Windows OpenSSH named pipe.
- The same protocol implementation works with 1Password, Bitwarden, and Keeper without
  provider-specific signing code.
- Keys loaded by KeePassXC into Windows OpenSSH agent work unchanged; Pageant works
  through its separate transport adapter.
- A configurable endpoint can use another RFC-compatible agent without a new build.
- A locked vault prompts through the provider; approval succeeds, denial returns a clear
  non-secret error, and reconnect requests a new signature.
- A pinned key succeeds when the agent exposes more than six identities and no unrelated
  key is offered to the server.
- Removing or rotating the pinned key produces a clear fingerprint-missing state and a
  key re-selection action.
- Session export contains only endpoint selection and public fingerprint metadata. It
  never contains agent-provided secret material.

### Standards and references

- [RFC 9987 — Secure Shell Agent Protocol](https://www.rfc-editor.org/rfc/rfc9987.html)
- [OpenSSH agent extensions](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL.agent)
- [OpenSSH `IdentityFile` and `IdentitiesOnly`](https://man.openbsd.org/ssh_config.5#IdentityFile)
- [1Password SSH agent and Bookmarks](https://www.1password.dev/ssh/bookmarks)
- [Bitwarden SSH agent limitations](https://bitwarden.com/help/about-ssh/)
- [KeePassXC SSH Agent integration](https://github.com/keepassxreboot/keepassxc/blob/develop/docs/topics/SSHAgent.adoc)
- [Keeper SSH Agent](https://docs.keeper.io/keeperpam/privileged-access-manager/ssh-agent)
- [Proton Pass SSH Agent](https://proton.me/support/ssh-agent)
- [FIDO Credential Exchange Format 1.0](https://fidoalliance.org/specs/cx/cxf-v1.0-ps-errata-20260309.html) — useful for future credential migration, not live agent session matching.

---

## 2. Native terminal surface via Microsoft Terminal

**Goal:** determine whether Microsoft's native terminal control can replace
WebView2 + xterm.js without replacing the WinUI shell, `ITerminalBackend`, SSH.NET,
ConPTY, recording, or session model.

Explore the Microsoft Terminal repository's `TerminalCore`, `AtlasEngine`, and
`HwndTerminal` path before considering GPUI, QuickJS + `@xterm/headless`, or a
new renderer. `HwndTerminal` already combines the native VT parser and text
buffer with DirectWrite, Direct3D/Direct2D rendering, TSF/IME, selection, and UI
Automation behind a small C ABI used by Microsoft's C# WPF wrapper.

### Candidate integration

- Keep local sessions on `LocalTerminalSession`/ConPTY and remote sessions on
  `SshTerminalSession`/SSH.NET. The control consumes their common terminal byte
  stream; it must not replace integrated SSH authentication, host-key trust,
  SFTP, SSHFS, or tmux handling with `ssh.exe`.
- Build a pinned, architecture-specific native DLL and host its child HWND from
  WinUI 3. Do not depend on the repository's `Windows.UI.Xaml` `TermControl`,
  which is not directly compatible with resesh's `Microsoft.UI.Xaml` controls.
- Put a narrow, versioned adapter around the native ABI. Do not expose Microsoft
  Terminal internals throughout the C# application.
- Preserve the current `TerminalControl` host contract so the native experiment
  can be compared with WebView2 without rewriting tab and backend lifecycle code.

### MVP spike

- `TerminalSurface` keeps the live-tab contract, with WebView2 as the default and
  `NativeTerminalSurface` selected only when `RESESH_TERMINAL_SURFACE=native`.
- The versioned C# adapter loads a pinned `Microsoft.Terminal.Control.dll` from
  `NativeTerminal/<architecture>` beside the app, or from
  `RESESH_NATIVE_TERMINAL_DLL`. A DLL cannot be loaded directly from another
  package's protected `WindowsApps` directory; the spike requires an app-local
  copy. No Microsoft binary is committed or shipped.
- The adapter supports both the Windows Terminal 1.24 focus exports and the
  current combined focus export. It incrementally decodes UTF-8 across backend
  chunks and preserves raw-byte recording timestamps before decode.
- Verified in the existing app: local ConPTY and SSH.NET sessions, keyboard
  input, output, backend resize, file-pane resize, focus/UI Automation, split
  reparenting, and teardown. The native child is hidden while connecting, locked,
  or rewinding so those XAML surfaces remain usable.
- Still blocked: keyframe/rewind serialization, OSC/title/prompt/agent events,
  links, highlights, search, command marks/ruler/panel, configurable scrollback,
  copy-on-select, full theme parity, and general XAML-over-HWND overlays.
  Performance, Unicode/IME, alternate-screen, mouse, DPI, x64/ARM64 packaging,
  and servicing measurements remain part of the full exploration acceptance.

### Questions to resolve

- Whether a child HWND can coexist reliably with tab dragging, split groups,
  lock/drop overlays, title-bar regions, focus restoration, DPI changes, and
  XAML content drawn above the terminal.
- How raw UTF-8 backend bytes are decoded and fed to TerminalCore without
  changing invalid-sequence, chunk-boundary, or recording behavior.
- Which extensions are required for OSC 7, OSC 3008, agent OSC events, title and
  command reporting, prompt discovery, links, highlights, and command marks.
- Whether complete terminal state can be captured and restored with the fidelity
  required by rewind and asciicast playback; the existing xterm serialize path
  has no equivalent in the published `HwndTerminal` ABI.
- Whether search, the annotated ruler, commands panel, notices, and playback UI
  can remain XAML surfaces without HWND airspace problems or renderer changes.
- The native build, packaging, servicing, x64/ARM64, and long-term fork cost.
  Microsoft Terminal's control is source-available under MIT but is not yet a
  stable, supported consumer package.

### Exploration acceptance

- A disposable spike opens both a local ConPTY tab and an SSH.NET tab in the
  existing WinUI application and exercises input, resize, scrollback, alternate
  screen, mouse reporting, copy/paste, Unicode, IME, DPI, splits, and teardown.
- The spike records measured startup, private memory, sustained-output
  throughput, and input-to-paint latency against the current WebView2 surface.
- A parity inventory identifies every current xterm/addon behavior as retained,
  adapted, reimplemented, or blocked. No production migration proceeds with
  silent feature loss.
- Native adoption is considered only if it clearly improves a measured product
  problem beyond lower-risk WebView2 work such as shared-buffer output and the
  xterm WebGL renderer.

### References

- [Microsoft Terminal code organization](https://github.com/microsoft/terminal/blob/main/doc/ORGANIZATION.md)
- [TerminalCore](https://github.com/microsoft/terminal/tree/main/src/cascadia/TerminalCore)
- [AtlasEngine](https://github.com/microsoft/terminal/tree/main/src/renderer/atlas)
- [`HwndTerminal` native ABI](https://github.com/microsoft/terminal/blob/main/src/cascadia/TerminalControl/HwndTerminal.hpp)
- [C# WPF host](https://github.com/microsoft/terminal/tree/main/src/cascadia/WpfTerminalControl)
- [Terminal control productization tracking](https://github.com/microsoft/terminal/issues/6999)
