# NoModCheck

An [AuroraPatch](https://github.com/Aurora-Modders/AuroraPatch) patch that stops Aurora 4X 3.3's anti-modding checks from quietly breaking features when the game runs under AuroraPatch.

## What 3.3 does

Aurora 3.3.0 added five checks. None of them shows a warning; each one silently disables part of the game:

| # | Check (3.3.0 obfuscated name) | Trigger | Effect |
|---|---|---|---|
| 1 | `l1.e()`, called from the time increment with a 1-in-100 roll | Harmony reports any patched Aurora method | For the rest of the session: standing orders stop running, naval commanders are no longer auto-assigned, and the Economics industry "Create" button does nothing |
| 2 | Naval Organization `Load` (`gk.ej`) | `Assembly.GetEntryAssembly()` isn't Aurora | The window opens but its setup is skipped (empty fleet tree) |
| 3 | Economics `Load` (`hu.ey`) | Same as above | The window opens but its setup is skipped (empty population list) |
| 4 | Shipyard "Create Task" (`hu.eb`) | The process file name doesn't end with `Aurora.exe` | The button does nothing |
| 5 | Commanders race selector (`a6.ab`) | Any `AuroraPatch` frame on the call stack | The selector does nothing (empty commander tree) |

Checks 2–5 fire whenever the game is started by AuroraPatch, even with no patches loaded. Check 1 needs at least one Harmony patch on Aurora.

## What NoModCheck does

- **Check 1:** a Harmony prefix makes the detection method return `false`.
- **Checks 2–5:** a transpiler rewrites the calls inside the affected Aurora methods only. The rest of the game is untouched.
  - `GetEntryAssembly()` returns the Aurora assembly.
  - `ProcessModule.FileName` returns the path of `Aurora.exe`.
  - `StackTrace.GetFrames()` returns the frames with AuroraPatch's frames removed.

The targets are found by what their code contains (the `"GetAllPatchedMethods"` string, and calls to those three APIs), not by obfuscated name, so the patch should keep working if a later 3.3.x build renames them. On versions before 3.3 it finds nothing and logs that.

## Install

1. Use an AuroraPatch build that supports 3.3 (see [Aurora-Modders/AuroraPatch](https://github.com/Aurora-Modders/AuroraPatch)).
2. Copy `NoModCheck.dll` into `Patches\NoModCheck\` in your Aurora folder.
3. Start the game with `AuroraPatch.exe`. `AuroraPatch.log` should contain:

```
Patch NoModCheck: l1.e: mod detection -> false
Patch NoModCheck: a6.ab: environment checks redirected
Patch NoModCheck: gk.ej: environment checks redirected
Patch NoModCheck: hu.ey: environment checks redirected
Patch NoModCheck: hu.eb: environment checks redirected
```

## Build

```
dotnet build -c Release -p:AuroraPatchDir=<folder containing AuroraPatch.exe>
```

The build targets .NET Framework 4.8, references `AuroraPatch.exe`, and compiles against Lib.Harmony 2.2.2, the version AuroraPatch ships.

## Testing

Tested on Aurora 3.3.0 (AuroraPatch checksum `FQ4CUl`), running a loaded game under AuroraPatch. Checks 2–5 were each tested with NoModCheck turned off and then on:

| | Without NoModCheck | With NoModCheck |
|---|---|---|
| Naval Organization | Fleet tree empty | Populated |
| Economics | Population list empty | Populated |
| Shipyard Create Task | No response | Reaches the game's own validation |
| Commanders race selector | Commander tree empty | Populated |

For check 1, the log confirms the detection method is patched at load. It hasn't yet been observed through a long enough run of increments for the 1-in-100 roll to fire.

## Please don't report bugs from a patched game

If you hit a bug, reproduce it in an unmodified Aurora before reporting it.
