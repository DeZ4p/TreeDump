<p align="center">
  <img src="Assets/logo-512.png" alt="TreeDump Pro logo" width="128" height="128">
</p>

<h1 align="center">TreeDump Pro</h1>

<p align="center">
  Project tree and context exporter for AI workflows.<br>
  Scan a codebase, pick what matters, stay inside your token budget, export clean context.
</p>

<p align="center">
  <a href="https://github.com/DeZ4p/TreeDump/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/DeZ4p/TreeDump/actions/workflows/ci.yml/badge.svg"></a>
  <img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4">
  <img alt="Platform" src="https://img.shields.io/badge/platform-Windows-0078D4">
  <img alt="Version" src="https://img.shields.io/badge/version-1.0.0-2563EB">
  <img alt="License" src="https://img.shields.io/badge/license-MIT-green">
</p>

---

<p align="center">
  <img src="
screenshot/pic.png
" alt="TreeDump Pro: workspace, export, search and settings" width="100%">
</p>

## Why

Feeding a project to an AI assistant usually means copying files by hand, guessing
how many tokens they cost, and accidentally pasting secrets. TreeDump Pro turns that
into a repeatable workflow: scan, select, budget, sanitize, export.

## Features

**Project tree**
- Fast folder scan with drag-and-drop of a project folder.
- Respects `.gitignore` (via `git status --ignored`) plus configurable ignored folders and extensions.
- Built-in code viewer/editor (AvalonEdit) with search across the project.
- Tracks which files were already sent to an AI session (SENT marker).

**Export**
- Text and code files only: images, video, audio, archives, binaries and key files are excluded.
- Formats: Markdown, XML, JSON, plain text.
- Selection by file size and by token budget, with a live meter and one-click Auto-fit.
- Automatic splitting into a sensible number of parts (Markdown / plain text).
- Optional options: directory tree, line numbers, comment stripping, code summary, minify, remove empty lines.
- Prompt presets (Architect, Bug hunt, Refactor, Custom) prepended to the export.

**Safety**
- Best-effort redaction of secrets (keys, tokens, passwords) in exported content.
- Sensitive files such as `.env*` are never auto-selected.
- Review the output before sharing it. Redaction is pattern-based and cannot be perfect.

**Session handoff**
- One-click handoff document summarizing the session and the files already sent.

**Interface**
- Dark, light and system themes with a WCAG 2.2 AA contrast target.
- Full keyboard support, themed dialogs, vector icons.

## Requirements

- Windows 10 or 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (to build from source)
- Git on `PATH` (optional, used for `.gitignore` awareness)

## Build and run

```powershell
git clone https://github.com/DeZ4p/TreeDump.git
cd TreeDump
dotnet build TreeDump.csproj -c Debug
dotnet run --project TreeDump.csproj
```

## Where things are stored

| What | Location |
|---|---|
| Settings and history database | `treedump.db` next to the executable |
| Logs | `%LocalAppData%\TreeDump\logs` |
| Exports and handoffs (running from source) | `<repo>\export\<PROJECTNAME>\` |
| Exports and handoffs (installed build) | `<exe folder>\export\<PROJECTNAME>\` |

None of these are tracked by Git (see `.gitignore`).

## Tech stack

- C# 12, .NET 8, WPF
- [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) for the code editor
- Entity Framework Core (SQLite) for local settings and history
- [SharpToken](https://github.com/dmitry-brazhenko/SharpToken) (`cl100k_base`) for token counting

Token counts are exact for `cl100k_base` and approximate for other models. The budget
meter in the Export window uses a fast estimate (about 4 bytes per token plus overhead).

## Repository layout

```
Assets/      logo, icon, vector source
Themes/      design tokens, control styles, logo drawing
Resources/   UI strings
Services/    application services
*.cs, *.xaml application source
```

## Roadmap

- Export only files changed in Git
- Export profiles and a project-level ignore file
- Command palette
- File watcher
- Single-file publish and installer
- Unit tests

## Author

| | |
|---|---|
| **Author** | DeZ4p |
| **GitHub** | https://github.com/DeZ4p |
| **Telegram** | https://t.me/DeZ4p |

## License

Released under the MIT License. See [LICENSE](LICENSE).