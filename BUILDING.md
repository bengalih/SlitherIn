# BUILDING.md — Compiling SlitherIn (new design)

This document covers the toolchain needed to compile the redesigned SlitherIn
project in this directory. The app you build is a Windows tray utility; the
output targets the **.NET Framework 4.8** runtime, so people who *run* the app
install nothing (4.8 is built into Windows 10/11). The SDK below is a developer
tool only.

## Requirements

| Requirement | Minimum | Notes |
|---|---|---|
| Windows | 10 or 11 | carries the .NET Framework 4.8 runtime (needed to build and run) |
| .NET SDK | 10.x (10.0.401 or newer) | installs the C# compiler (Roslyn), MSBuild, and the `dotnet` CLI |
| NuGet access | internet on first build | packages are downloaded automatically at restore |

No Visual Studio is required. Any editor (VS Code, Notepad++, etc.) works.

### Installing the .NET SDK

```
winget install Microsoft.DotNet.SDK.10
```

or download from <https://dotnet.microsoft.com/download/dotnet/10.0>.
Verify with `dotnet --list-sdks` (expect `10.0.x`).

## NuGet packages (restored automatically on first build)

- **Microsoft.NETFramework.ReferenceAssemblies** (1.0.3) — build-time only.
  Supplies the .NET Framework 4.8 reference assemblies so the 4.8 "Developer
  Pack" is **not** required to compile.
- **System.Text.Json** (10.0.x — supports .NET Framework 4.6.2+ / netstandard2.0,
  so 4.8 works). Runtime serializer. JSON comments (`//`, `/* */`) are handled
  natively via `JsonCommentHandling.Skip` — no comment-stripping pre-pass.

Note: both download automatically on the first `dotnet build` (needs internet).
Later builds work offline once cached.

## Build

```
.\build.ps1
```

or manually:

```
dotnet build src -c Release
```

`build.ps1` does: stop any running `slitherin.exe` → `dotnet build -c Release`
(full solution: app + tests) → **REV-verify** (parses `rev-YYYYMMDD-NN` from
`src\Program.cs`, reads the built EXE bytes, UTF-16 decodes, asserts the EXE
contains that REV) → relaunches the EXE.

Output: `src\bin\Release\net48\slitherin.exe` (WinExe — no console window).

## Tests

```
dotnet test tests\SlitherIn.Tests -c Release
```

## Run / test

Launch `slitherin.exe` (or `build.ps1` does it for you). It runs in the tray.
There is no runtime installation step for users.

## Version (REV) convention

`REV` is a string like `rev-YYYYMMDD-NN` defined in `src\Program.cs`. Bump it on
every code change before building — it is shown in the tray menu and verified by
`build.ps1` after each build (the build-ran proof).

## Notes

- The older build path (in-box `.NET Framework` `csc.exe`, C# 5 ceiling, classic
  MSBuild 4.0 project) is **retired**.