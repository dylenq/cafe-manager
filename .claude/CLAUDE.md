<!-- GSD:project-start source:PROJECT.md -->

## Project

**Cafe Manager**

Cafe Manager is local management software for a small gaming cafe (under 15 stations): Windows gaming PCs plus consoles connected to Samsung Tizen smart TVs. Front-desk staff use one browser-based admin app to start and bill timed sessions, lock and unlock PCs, power off console TVs when time runs out, manage members and prepaid time, sell food and drinks, print receipts and see revenue. It runs entirely on a server in the cafe, so it keeps working when the internet drops.

**Core Value:** Staff can start a timed session on any station, and that station reliably stops being usable when the paid time runs out: the PC locks or the TV powers off.

### Constraints

- **Tech stack**: C# / .NET 10 — the user's choice, and a strong fit for the Windows agent and the Windows-service host.
- **Server**: ASP.NET Core + Blazor (interactive server), EF Core + SQLite, SignalR — local, single machine, no external DB to run.
- **Agent platform**: Windows only (WPF); it can't be run on the macOS dev machine, so the simulator covers the PC flow during development.
- **Network**: Local LAN only; it must keep working without internet.
- **Scale**: Fewer than 15 stations, one location; no need to scale beyond that.

<!-- GSD:project-end -->

<!-- GSD:stack-start source:STACK.md -->

## Technology Stack

Technology stack not yet documented. Will populate after codebase mapping or first phase.
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->

## Conventions

Conventions not yet established. Will populate as patterns emerge during development.
<!-- GSD:conventions-end -->

<!-- GSD:architecture-start source:ARCHITECTURE.md -->

## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->

## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->

## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->

<!-- GSD:profile-start -->

## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->
