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
| Version is deprecated | ⚠ Ask first |
| Version has known vulnerabilities | ⚠ Ask first |

## Install

```bash
dotnet tool install -g NuBouncer
```

## Check a package

```bash
nubouncer check Newtonsoft.Json
nubouncer check Serilog --version 4.0.1
```

Exit codes: `0` fine, `1` warnings, `2` blocked.

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

## Privacy

NuBouncer only talks to the public nuget.org API to look up package details. It sends nothing else anywhere.

## Licence

MIT
