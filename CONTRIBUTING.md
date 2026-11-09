# Contributing to TreeDump Pro

Thanks for helping out. This document keeps contributions consistent and easy to review.

## Setup

Requirements: Windows 10/11, [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), Git.

```powershell
git clone https://github.com/DeZ4p/TreeDump.git
cd TreeDump
dotnet build TreeDump.csproj -c Debug
dotnet run --project TreeDump.csproj
```

Close the running app before building. A running `TreeDump.exe` locks the output files.

## Before opening a pull request

- The build must finish with **0 Warning(s) and 0 Error(s)**.
- Run the app and test the feature you changed.
- Do not commit `treedump.db`, logs, `export\`, `bin\`, `obj\` or `*.bak` files.
- Keep each pull request focused on one change.

## Code guidelines

- C# with `Nullable` enabled. Do not suppress warnings to make the build pass; fix the cause.
- Source code, comments and UI strings are in English.
- Do not hardcode secrets, tokens or keys. Do not log secret values.
- Use the design tokens and styles from `Themes\` instead of literal colors and fonts.
- UI icons come from the `Segoe Fluent Icons, Segoe MDL2 Assets` font. No emoji as icons.
- Dialogs use the themed in-app dialogs, not the default `MessageBox`.

## Accessibility

- Text contrast of at least 4.5:1 and control contrast of at least 3:1 (WCAG 2.2 AA).
- Everything reachable by keyboard. `Esc` closes modal windows, `Enter` confirms (`IsCancel` / `IsDefault`).
- Visible focus indicator of at least 2 px.

## Commit messages

Short, imperative, and specific. Prefixes are welcome:
`feat:`, `fix:`, `docs:`, `refactor:`, `chore:`.

## Reporting bugs and ideas

Use the issue templates. For security problems, follow [SECURITY.md](SECURITY.md) instead.