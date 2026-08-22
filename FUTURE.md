# Resesh Future

Ideas that are worth preserving but are not committed roadmap work. When an item is
scheduled, move it to [ROADMAP.md](ROADMAP.md) and keep implementation decisions in
[DECISIONS.md](DECISIONS.md).

---

## 1. Password-manager-backed SSH keys

**Goal:** let a Resesh session authenticate with an SSH key held by 1Password,
Bitwarden, KeePass/KeePassXC, Keeper, Proton Pass, or another compatible agent. The
private key and its passphrase must never enter Resesh storage.

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
  managed tunnels and key rotation, but Resesh only needs the agent boundary.
- **KeePassXC** — it loads database-backed keys into Windows OpenSSH agent or Pageant;
  KeePassXC does not provide a separate agent endpoint.
- **KeePass with KeeAgent** — use its OpenSSH-compatible or Pageant interface. Do not
  depend on KeePass URL overrides or plugin-specific entry fields.
- **Proton Pass** — its desktop app generates the endpoint setup command. Treat it as a
  configurable compatible endpoint until its Windows endpoint contract is documented.

Provider detection is useful for display and setup help, but it must not change the
wire behavior. Unknown RFC-compatible agents must work without a provider adapter.

### Session and key identity

Resesh already owns the destination identity: session id, display name, username,
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
not contain a friendly hostname. Resesh can show the destination in its own UI, but must
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
