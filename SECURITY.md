# Security Policy

## Supported versions

Only the latest release receives security fixes.

| Version | Supported |
|---|---|
| 1.0.x | Yes |

## Reporting a vulnerability

Please do **not** open a public issue for security problems.

1. Preferred: use GitHub's private reporting. Open the **Security** tab of this
   repository and choose **Report a vulnerability**.
2. Alternative: contact the author on Telegram: https://t.me/DeZ4p

Include the affected version, steps to reproduce, and the impact you expect.
You can expect an initial reply within a few days. Fixes are published as a new
release, and reporters are credited unless they ask not to be.

## Scope and known limits

TreeDump Pro works on local files and does not send data over the network.

- **Secret redaction is best-effort.** It uses pattern matching and cannot catch
  every secret format. Always review an export before sharing it.
- **Local data.** Settings and history are stored in `treedump.db` next to the
  executable, and logs in `%LocalAppData%\TreeDump\logs`. Treat both as private.
- **Exports** can contain source code. Keep the `export` folder out of version control.

Reports about missed secret formats are welcome and count as valid issues.