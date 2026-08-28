---
name: fluxreader-project-guidelines
description: Apply FluxReader project conventions and coding style when planning, implementing, reviewing, or refactoring changes in this repository.
---

# FluxReader Project Guidelines

- Target Windows 11 only. Do not add Windows 10 or legacy compatibility.
- Prefer the newest supported Windows, WinUI 3, and .NET approach, with the simplest modern architecture.
- Do not add compatibility shims, fallback renderers, degraded modes, parallel implementations, or legacy workarounds unless the user explicitly chooses them.
- If the intended architecture fails, diagnose and explain the likely causes and options first, then let the user decide whether to adopt an alternative.
- Do not optimize prematurely.
- Prefer official WinUI 3 controls, styles, theme resources, behaviors, and supported APIs when they satisfy the requirement, and ensure UI work follows Fluent Design principles and Windows platform conventions. Do not replace them with custom colors, control templates, workarounds, or compatibility patches unless the official option is demonstrably insufficient; when customization is necessary, keep it minimal and document why.
