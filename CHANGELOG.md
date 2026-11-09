# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-11-10

First public release.

### Added
- Project tree scanner with drag-and-drop, `.gitignore` awareness and configurable ignored folders and extensions.
- Built-in code viewer and editor (AvalonEdit) with project-wide search.
- SENT tracking for files already shared with an AI session.
- Export window: text and code files only, Markdown / XML / JSON / plain text output.
- Selection by file size and by token budget, live budget meter and one-click Auto-fit.
- Automatic splitting of Markdown and plain text exports into a reasonable number of parts.
- Export options: directory tree, line numbers, comment stripping, code summary, minify, empty line removal.
- Prompt presets: Architect, Bug hunt, Refactor and Custom.
- Best-effort redaction of secrets in exported content.
- Session handoff document generator.
- Dark, light and system themes with a WCAG 2.2 AA contrast target.
- About window with application logo, version and author details.
- Export and handoff output under `export\<PROJECTNAME>\`.

### Known limitations
- Secret redaction is pattern-based. Review exports before sharing them.
- Token counts in the Export window are fast estimates. Exact counting uses `cl100k_base`.
- Windows only.

[1.0.0]: https://github.com/DeZ4p/TreeDump/releases/tag/v1.0.0