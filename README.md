# NuBouncer

**Checks NuGet packages' ID at the door.**

AI coding assistants sometimes suggest NuGet packages (or versions) that don't exist. Attackers register those made-up names with malware in them, an attack called *slopsquatting*. NuBouncer stops your AI agent adding packages that don't exist or look dodgy, before they're installed.

## What it checks

| Check | Result |
|---|---|
| Package doesn't exist on nuget.org | ✗ Blocked |
| Requested version doesn't exist | ✗ Blocked |
| First published less than 30 days ago | ⚠ Ask first |
| Fewer than 1,000 downloads | ⚠ Ask first |
| Uses a trusted prefix like `Microsoft.` without a verified owner | ⚠ Ask first |
| Version has known vulnerabilities | ⚠ Ask first |
| Version is deprecated because of critical bugs | ⚠ Ask first |
| Version is deprecated as legacy (e.g. xunit 2 → v3) | ℹ Note only |
| Popular package using a trusted prefix, not from its owner | ℹ Note only |

## Install

```bash
dotnet tool install -g NuBouncer
```

## Check a package

```bash
nubouncer check Newtonsoft.Json
nubouncer check Serilog --version 4.0.1
```

## Scan a whole codebase

```bash
nubouncer scan            # current folder
nubouncer scan path/to/repo
```

Checks every package in every `.csproj`, `.fsproj`, `.vbproj`, `Directory.Packages.props` and `packages.config` under the folder. Handy in CI:

```yaml
- run: dotnet tool install -g NuBouncer
- run: nubouncer scan
```

Exit codes for `check` and `scan`: `0` fine, `1` warnings, `2` blocked.

## Use with Claude Code

Add this to `~/.claude/settings.json` (all projects) or `.claude/settings.json` (one project):

```json
{
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash|PowerShell|Edit|Write|MultiEdit",
        "hooks": [{ "type": "command", "command": "nubouncer hook", "timeout": 30 }]
      }
    ]
  }
}
```

NuBouncer then checks every `dotnet add package`, `dotnet package add`, `nuget install` and `Install-Package` command, and every edit that adds a package to a `.csproj`, `Directory.Packages.props` or `packages.config`.

- **Blocked** packages are denied, and Claude is told why so it can fix the name.
- **Warnings** make Claude Code ask you before going ahead.
- **Everything else** is silent.

## Private packages and settings

Create a `.nubouncer.json` in your repo (NuBouncer looks in the current folder and upwards):

```json
{
  "allow": ["MyCompany.*"],
  "minDownloads": 1000,
  "minAgeDays": 30
}
```

## How well does it work?

Tested against live nuget.org (November 2026):

- **No false alarms** on NuGet's 100 most-downloaded packages, 100 packages ranked around #1,000, or 100 niche PDF/CSV packages.
- **Blocked all 18 genuinely made-up names** from a list of 20 plausible AI-style guesses, such as `AutoMapper.Extensions.DependencyInjection` and `Polly.Extensions.Retry`. The other two turned out to exist.

The test projects are in [`validation/`](validation). Run `nubouncer scan validation/top100` to try it yourself.

## Privacy

NuBouncer only talks to the public nuget.org API to look up package details. It sends nothing else anywhere.

## Licence

MIT
