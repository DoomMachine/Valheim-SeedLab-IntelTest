# natives - values recorded from the game's own code

These files are the "native-function goldens" SeedLab checks its port against: results that
**Valheim 1.0.15 itself** produced for the handful of functions world generation depends on and whose
code is not in the game's managed assembly (they live in Unity's native player or in Mono's maths
library). They were recorded by SeedLab's dumper plugin, running inside the game on **2026-09-22**,
from the build stamped in every file:

```
DATA-STAMP game-version=1.0.15 network=40 unity=6000.0.75f1
assembly_valheim-sha256=59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1
unityplayer-sha256=4d161e15d8ccdb32eb73262e7a3e0a66f8c175b50a38e22ae5b0e8fb9aea98f3
dumped=2026-09-22 dumper=1.0.0
```

A machine that reproduces every value here bit for bit computes these functions exactly as the game
does. The machine report replays them twice: through SeedLab's machine self-test (the suite `vseed`
registers, `vendor/src/SeedLab.Cli/Infra/NativesSuite.cs`) and through SeedLab's 11-check natives gate
(`vendor/tests/SeedLab.Tests/NativesGoldens.cs`).

They contain no game assets and no game files - only numbers, and the names of the prefabs whose
string hashes were recorded. They are included at the repository owner's request.

## The files

| File | What it holds | Checked by |
| --- | --- | --- |
| `natives-perlin.bin` | 262,780 samples of Unity's `Mathf.PerlinNoise` / `PerlinNoise1D` as raw float32 bit patterns: `"VPL1"`, schema, block count, then `{x, y, result}` triples | both |
| `natives-perlin.json` | the index of that file: four blocks (lattice points and extremes, a 512 x 512 grid, and the exact arguments world generation uses), each with its sample count and byte offset, and the `.bin` file's SHA-256 | both |
| `natives-random.json` | `UnityEngine.Random`: the state round trip, `InitState` for 268 seeds, and 276 call traces with the four state words after every draw (1,980 draws, including one after 100,000 discarded draws) | natives gate |
| `natives-half.json` | 1,635 `Mathf.FloatToHalf` / `HalfToFloat` results, including rounding ties and NaN | natives gate |
| `natives-libm.json` | 93 results of Mono's `Math.Sin/Cos/Atan2/Pow` at the arguments world generation uses, and 49 `WorldGenerator.WorldAngle` values | both |
| `natives-hash.json` | `GetStableHashCode` of 428 strings: 232 location, 164 vegetation and 32 alternative-biome prefab names | both |
| `version-constants.json` | the game's world, world-generation and minimap version numbers and grid constants | (reference only) |
| `manifest-natives.json` | the dump's manifest: the build stamp, the dumper's version, and each file's size and SHA-256 | (reference only) |

Every value that is a float is stored as its bit pattern (a decimal copy sits beside it for people);
the checks compare bit patterns, never decimals.

## Where the seeds in these files come from

No value here was taken from a world anyone plays. The files come from two runs of the dumper, and
each file's stamp says which (`mode=natives` or `mode=assets`):

- `natives-perlin.bin`, `natives-perlin.json`, `natives-random.json`, `natives-half.json`,
  `natives-libm.json` and `manifest-natives.json` come from the natives run, made at the game's main
  menu with no world loaded (`manifest-natives.json`: `"world": null`);
- `natives-hash.json` and `version-constants.json` come from a later assets run, made while a world
  was loaded. Neither depends on that world: the prefab names are the game's own, and the version
  numbers and grid constants are compiled into the game. The one entry that did describe the loaded
  world was removed (below).

In the natives run:

- the 268 `InitState` seeds and `worldgen-ctor` traces are exactly the dumper's fixed corpus: 0, 1, -1,
  int.MinValue, int.MaxValue, the two SeedLab test worlds' seeds (-1772362158 and 319486907, created
  for SeedLab's verification and already published in its source), the first test world's river and
  stream seeds (744350289, 952983356), 920, 7, 12345, and 256 values from a fixed SplitMix64 sequence
  - checked by recomputing the sequence;
- the Perlin block of "real arguments" uses the fixed offsets of that first test world, not a live
  world's offsets (a live world would have added 245 samples; the block has the fixed 588).

## What was changed from the recording, and why

This folder is a copy of SeedLab's `groundtruth\natives`, which is not published with SeedLab's source.
Before it was published here, every file was read for personal data: names, account and folder paths,
computer names, Steam, PlayFab and player IDs, world UIDs, e-mail and IP addresses. Two files were
changed; the other six are byte-for-byte as recorded.

1. **`natives-hash.json` - one entry removed.** Besides the 428 prefab names, the file held one entry of
   kind `seedtext`: the seed name of the world that was loaded when the dumper recorded its game data,
   with its hash - and a seed name's hash *is* that world's seed. It describes a world on the machine
   that made the recording rather than the game, so it was removed. It adds nothing to the check: the
   428 prefab names exercise the same function. (SeedLab's gate still words this check as "428/428
   prefab names and seed texts hash identically": its code is vendored unmodified, but no seed text
   remains.) The removal was made by loading the file and writing it back with the same formatting
   (verified first: an unchanged round trip reproduced the original file byte for byte), so the
   entry and the comma before it are the only difference. The file went from 40,377 bytes (SHA-256
   `c51455f9...`) to 40,290 bytes (`5b11d7af5e7d6f8231c46c9e6b720dbff376ee739c75627ce6e6161352c2eea3`).
2. **`manifest-natives.json` - `files[]` rewritten, one note added.** The recorded list did not match
   this folder: it named files under a `goldens/` prefix that does not exist here, listed a
   `seed-input.json` that is not included (it describes a world, not a native function), left out
   `natives-hash.json` (written by a later run of the dumper), and recorded a 716-byte
   `version-constants.json` where the folder holds the 715-byte copy a later run rewrote. The list now
   names exactly the seven data files here, flat, with sizes and SHA-256 recomputed from them, and a
   note in `notes[]` says so. The `stamp`, `game`, `dumper` and other fields are unchanged.

`natives-perlin.json` still carries the `.bin` file's SHA-256 exactly as recorded, and it still matches.
