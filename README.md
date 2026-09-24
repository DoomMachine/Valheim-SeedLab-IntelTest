# SeedLab machine report

A small, self-contained Windows program that checks whether a PC reproduces
[SeedLab](https://github.com/DoomMachine/Valheim-SeedLab)'s world-generation arithmetic **bit for bit**,
at every instruction-set level the processor has, and measures how fast it runs. SeedLab reproduces
Valheim's world generation offline; its exactness has been proven on one machine (an AMD processor),
and this package collects the same evidence from other machines - Intel ones first - without needing
the game on them. Nothing is installed: the package carries its own copy of .NET.

## If you were asked to run it

1. **Download** `SeedLab-MachineReport-1.0.0-win-x64.zip` (about 36 MB) from this repository's
   [Releases](https://github.com/DoomMachine/Valheim-SeedLab-IntelTest/releases) page.
2. **Check the download.** The release lists the zip's SHA-256 (also in its `SHA256SUMS.txt`). In
   PowerShell, in the folder holding the zip:

   ```
   Get-FileHash .\SeedLab-MachineReport-1.0.0-win-x64.zip
   ```

   The `Hash` it prints must be the same as the one on the release (`Get-FileHash` prints capital
   letters and `SHA256SUMS.txt` small ones; the case makes no difference).
3. **Unblock, then extract.** Right-click the zip, *Properties*, tick *Unblock* if it is there, *OK*;
   then right-click, *Extract All...*. Unblocking first avoids most Windows warnings.
4. **Close other programs** (games, browsers, video) - part of the test measures speed - and plug a
   laptop into mains power.
5. **Run** `Run SeedLab machine report.bat` in the extracted folder. If Windows shows a blue *"Windows
   protected your PC"* box (SmartScreen), click **More info**, then **Run anyway**: the program is new
   and not code-signed, which is all that box means. If an *"Open File - Security Warning"* box asks,
   click *Run*.
6. **Wait about 3 to 5 minutes.** A console window shows the progress; leave the PC alone meanwhile.
   When it says *Finished*, press a key to close it.
7. **Send back `seedlab-machine-report.txt`** from the same folder. That one file is all that is
   needed. It is plain text - open it in Notepad first if you like.
8. **Delete the folder and the zip** afterwards. The program leaves nothing anywhere else.

**What it records about the PC:** the processor's name and nominal clock speed (from the registry),
CPUID identification (vendor, family, model, stepping and feature bits), cores and threads
(including performance and efficiency cores), the instruction sets .NET sees, total memory, the
Windows edition, version and build (from the registry), the version of Windows' C runtime
(`System32\ucrtbase.dll`), whether Windows reports a battery and whether the PC is on mains power,
and how busy the processor is during 3 seconds before the checks. It also goes through the
process's environment variables to find .NET settings (`DOTNET_*`, `COMPlus_*`, `CORECLR_*`),
which every check runs without; of those it records the names only, plus the value of a short
on/off switch such as `DOTNET_EnableAVX2`. SeedLab's own hardware probe, which its self-test runs,
also reads how much memory is in use; that is not written to the report.

**What it does not record:** your user name or computer name, serial numbers, product keys, network
adapters or addresses, or any path outside the package folder; and it opens none of your files.
Before the report is written, every piece of text in it is filtered so that the package folder
appears as `<package>` and any other folder path is withheld, up to the end of its line or its
quoted string.

**What it writes:** only inside its own folder - a temporary `work` folder, deleted when it finishes,
and the report. It installs nothing, changes no setting, and does not use or touch any .NET already
on the PC.

If it says *"You must install .NET to run this application"*, the `dotnet` folder did not extract
completely (or security software removed a file from it): extract the zip again into a short folder
such as `C:\SeedLabTest`. If the window closes at once or shows an error, send a screenshot of it.

## What the program does

`app\SeedLab.MachineReport.exe`, started by the `.bat` with the package's own runtime:

1. **Checks the package**: every file against `package-files.sha256`, written when the package was
   built, and that the .NET runtime it is running on is the one in `dotnet\` - by the folder CoreLib
   was loaded from and the folder of every native runtime module in the process.
2. **Runs the checks at four instruction-set levels**, each in a child process started with a clean
   environment (no `DOTNET_*`, `COMPlus_*` or `CORECLR_*` setting from the machine is inherited):

   | Level | Switches | Meaning |
   | --- | --- | --- |
   | `as-found` | none | whatever .NET uses on this CPU (AVX-512 where present) |
   | `no-avx512` | `DOTNET_EnableAVX512=0`, `DOTNET_EnableAVX512F=0` | AVX2 and below |
   | `no-avx2` | `DOTNET_EnableAVX2=0` | AVX and below; SeedLab's 8-wide Perlin path is off |
   | `scalar` | `DOTNET_EnableHWIntrinsic=0` | no SIMD instruction set at all |

   Each child reports the instruction sets it really got, so a switch that is ignored shows up as
   a failed check rather than as a level that silently was not tested. (.NET 10.0.12 ignores
   `DOTNET_EnableAVX512F` on its own - every `Avx512*` class stays supported - which is why
   `DOTNET_EnableAVX512` is set as well.) At every level the child runs:
   - SeedLab's **Perlin startup self-test** (the three Perlin spellings, scalar and 8-wide, agree);
   - SeedLab's **machine self-test**: 271 recorded libm and float-evaluation vectors, plus the natives
     suite `vseed` registers - 263,778 checks against values the game itself produced;
   - SeedLab's **11-check natives gate**: Mathf.PerlinNoise, UnityEngine.Random, FloatToHalf,
     Mono's libm and GetStableHashCode, replayed against `natives\`;
   - seven **world fingerprints**, SHA-256 over every sampled value: biome and base height on a
     1024 x 1024 grid at 24 m for three seeds, heights after pre-generation on a 512 x 512 grid at
     48 m for two, and the pre-generated lakes, rivers and streams of those two. They are compared
     with `reference\fingerprints.json`.
3. **Times** the two costs every SeedLab search is made of - a whole-world biome grid (256 x 256
   points), on one thread, on half the logical processors and on all of them, and a world's river
   pre-generation, on one thread and on all of them - three runs of 10 s each per configuration,
   reporting the median and the spread.
4. **Writes `seedlab-machine-report.txt`**: a plain summary (every check PASS or DIFFERENT, with its
   numbers), then one JSON block with all the data, between `-----BEGIN SEEDLAB MACHINE REPORT JSON-----`
   and `-----END SEEDLAB MACHINE REPORT JSON-----`. Exit code 0 means every check passed, 1 that some
   were DIFFERENT, 2 a bad command line, 3 that the program itself failed.

## Building it

Requirements: Windows x64 and the .NET 10 SDK with the 10.0.12 runtime installed (the SDK's
`Microsoft.NETCore.App.Host.win-x64` apphost pack is used; no win-x64 runtime pack and no NuGet
package is needed - nothing is downloaded).

```
powershell -ExecutionPolicy Bypass -File tools\build-package.ps1
```

produces `dist\SeedLab-MachineReport-1.0.0\`, `dist\SeedLab-MachineReport-1.0.0-win-x64.zip` and
`dist\SHA256SUMS.txt` (`dist\` is not committed). The script deletes every `bin\` and `obj\` first
(an incremental build can keep a stale apphost), publishes the program framework-dependent for
win-x64, copies the installed runtime - `dotnet.exe`, `host\fxr\10.0.12`,
`shared\Microsoft.NETCore.App\10.0.12`, `LICENSE.txt`, `ThirdPartyNotices.txt`, unmodified - into
`dotnet\`, proves the program starts on that copy with `DOTNET_ROOT` pointing nowhere, and zips the
result with sorted entries and fixed timestamps.

The build is reproducible and carries no trace of the machine that made it: `Deterministic`, every
source path mapped to `/_/` (`PathMap`), no debug symbols, and no source-control data embedded
(`Directory.Build.props`). The only path inside a shipped binary is Microsoft's: the apphost,
`app\SeedLab.MachineReport.exe`, is the SDK's own apphost, and its debug directory names the file
of Microsoft's build of it (`D:\a\_work\1\s\...\apphost.pdb`); the four DLLs carry no path. The
apphost is built with `AppHostDotNetSearch=AppRelative` and `AppHostRelativeDotNet=..\dotnet`, so
it looks for .NET **only** in the package's `dotnet\` folder; the `.bat` also sets `DOTNET_ROOT`
to that folder.

`-MakeReference` first rebuilds `reference\fingerprints.json` by running the program's reference mode
on the build machine; it refuses to write the file unless every check passes at every level, every
level reaches a different instruction-set tier, and all four levels give identical fingerprints. The
file records the machine it was made on (processor, instruction-set levels, Windows version) and the
exact definition and byte layout of every fingerprint. The program option behind it,
`--make-reference <file>`, writes the file it is given, wherever that is; a tester's run (the `.bat`
with no options) writes only the `work` folder and the report, inside the package.

## Repository layout

| Path | What it is |
| --- | --- |
| `tools/SeedLab.MachineReport/` | the program |
| `tools/package/` | the launcher `.bat` and the tester's `README-FIRST.txt` |
| `tools/build-package.ps1` | builds the package and its zip |
| `vendor/` | SeedLab source, unmodified, from commit `11aeb8f` - see [VENDORED.md](VENDORED.md) |
| `natives/` | the values recorded from Valheim's own code that the checks replay - see [natives/README.md](natives/README.md) |
| `reference/fingerprints.json` | the reference fingerprints and the machine they were made on |
| `THIRD-PARTY-NOTICES.md` | FastNoise (MIT), the bundled Microsoft .NET runtime's licence and notices, the re-created Valheim and Unity code in `vendor/`, Valheim |

## Credits

The SeedLab machine report was conceived and directed by DoomMachine, who also recorded - in their
own copy of Valheim - the game values in `natives\` it checks against.

The code, tests and documentation were written by Claude, Anthropic's AI model, working in Claude
Code under DoomMachine's direction. Commits are authored by DoomMachine; Claude is credited here
rather than as a co-author.

Valheim is a trademark of Iron Gate AB. This project is independent, not affiliated with or endorsed
by Iron Gate or Coffee Stain. Third-party material: THIRD-PARTY-NOTICES.md (FastNoise, MIT; the
Microsoft .NET runtime bundled in the release package; the port of the game's world generation).

## License

MIT - see [LICENSE](LICENSE). The Microsoft .NET runtime in the release package's `dotnet\` folder is
Microsoft's, redistributed unmodified under the terms in its own `LICENSE.txt`.
