# Vendored SeedLab source

The program in `tools/SeedLab.MachineReport` runs SeedLab's own code, not a copy rewritten for it. That
code is vendored here, **unmodified**, from the SeedLab repository:

| | |
| --- | --- |
| Repository | https://github.com/DoomMachine/Valheim-SeedLab |
| Commit | `11aeb8fad8427730a065f56f1fa4353d0e1525a3` (`11aeb8f`, 2026-09-24) |
| Licence | MIT (the same `LICENSE` as this repository) |
| How it was taken | `git archive 11aeb8f <paths>`, extracted into `vendor/` - the files below, byte for byte |

Upstream paths are kept under `vendor/`, so `vendor/src/SeedLab.WorldGen/WorldGeneratorPort.cs` is
upstream's `src/SeedLab.WorldGen/WorldGeneratorPort.cs`.

## What is vendored, and why

The smallest set that builds the checks this program runs:

- **Three whole projects**, referenced by the program as projects:
  - `src/SeedLab.WorldGen` - the port of Valheim's `WorldGenerator` and Unity's native functions
    (Perlin noise with its SIMD dispatch and startup self-test, `Random`, the maths helpers). Every
    fingerprint and every golden replays through it.
  - `src/SeedLab.Seeds` - `GetStableHashCode`, which the natives checks replay.
  - `src/SeedLab.Runtime` - the machine self-test (`SelfTest/`, with its 271 recorded libm and
    float-evaluation vectors) and the hardware probe it uses. The rest of the project (resource
    modes, cost estimation, storage, the throttle) comes with it because it is one project; this
    program never calls it. In particular it builds the self-test with no cache folder, so the
    self-test's pass stamp - which SeedLab keeps under `%LOCALAPPDATA%\SeedLab` - is never written.
- **Three single files**, compiled into the program:
  - `src/SeedLab.Cli/Infra/NativesSuite.cs` - the generator-level golden suite `vseed` registers with
    its machine self-test. It finds its folder through SeedLab.Cli's `Verified.FindGroundTruth()`;
    that class is not vendored (it also carries the game build stamp and looks for a Valheim install),
    so `tools/SeedLab.MachineReport/Shims/Verified.cs` provides just that one member, answering with
    the package folder. The program then checks that the suite really read the package's `natives\`.
  - `tests/SeedLab.Tests/NativesGoldens.cs` - SeedLab's 11-check native-function gate
    (`dotnet run --project tests\SeedLab.Tests -- natives` upstream). It reads the folder named by
    `SEEDLAB_NATIVES_DIR`, which the program sets, for its own process only, to the package's `natives\`.
  - `src/SeedLab.Saves/Half16.cs` - the binary16 decoder that gate's `HalfToFloat` check calls. The
    rest of `SeedLab.Saves` (the save-file readers) is not needed and not vendored.

Not vendored, deliberately:

- `Directory.Build.props` (upstream root) - its compiler and runtime settings (`LangVersion latest`,
  `Nullable enable`, `ImplicitUsings disable`, `InvariantGlobalization true`, no documentation file)
  are repeated in this repository's root `Directory.Build.props`, which adds the reproducible-build
  settings. A vendored copy under `vendor/` would have shadowed that file for the vendored projects.
- `src/SeedLab.Runtime/README.md` - documentation only; nothing builds from it.
- Everything else in SeedLab: the command line, the web map, search, location placement, the game-data
  loader, the save readers, the dumper, and the tests other than the one file above.

Two comments in the vendored files name one of SeedLab's two ground-truth test worlds by its world
name (`src/SeedLab.WorldGen/Unity/UnityRandom.cs` line 109, `src/SeedLab.Saves/Half16.cs` line 13).
They are left as they are, because the files are unmodified copies.

## The files

52 files, with the size in bytes and the SHA-256 of each as vendored:

| Upstream path | Bytes | SHA-256 |
| --- | ---: | --- |
| `src/SeedLab.Cli/Infra/NativesSuite.cs` | 13,289 | `f086622c5f97a2f787d17158b9ac8847d7e1162552a8603608a6b27aeef16710` |
| `src/SeedLab.Runtime/Estimation/Calibration.cs` | 5,271 | `575f3779cc4d10fa0676a67d8891bcd7a18a8845759d33550de4d457614c7941` |
| `src/SeedLab.Runtime/Estimation/CostModel.cs` | 12,106 | `58adedf3548d620a89fb4b568bcd5d89d800d28e6a2257b18db02c14e4a46b91` |
| `src/SeedLab.Runtime/Estimation/Estimate.cs` | 6,835 | `36b41892bc09aa3d3700f26006262135c5e3de41342e76afd09ea2841d1d2085` |
| `src/SeedLab.Runtime/Estimation/EstimateInputs.cs` | 4,277 | `53386046fec89456056defbb19eec6cddf69bd09db9b1508325e958653ee5de0` |
| `src/SeedLab.Runtime/Estimation/Estimator.cs` | 14,131 | `85b0611e447f93c7c1896dab4a24327e43ea65398a4cbd46b112e2ce1c5196bc` |
| `src/SeedLab.Runtime/Execution/ResourceMode.cs` | 3,377 | `bef0961054d3d4f737450a800ce2d5cd1b273f7b221e33cec42d2769512fe837` |
| `src/SeedLab.Runtime/Execution/WorkerFootprints.cs` | 6,525 | `2ba7924966c5639ac4869e7d1df28e6bc05500c8704b10bf237bedee43aebc64` |
| `src/SeedLab.Runtime/Execution/WorkerPlan.cs` | 11,129 | `df4bb11cf0e0709b5668db93668b78b8155b7647f372bd5b800e39fa803cd6f8` |
| `src/SeedLab.Runtime/Hardware/Bytes.cs` | 2,610 | `47568897c3a6d020ec65a53355bcb5d46bef40541cab91ae78223e75aa9ec576` |
| `src/SeedLab.Runtime/Hardware/HardwareInfo.cs` | 8,811 | `eae2912c847535c3c83c7cf03359d5df24c38b709ef64982496170ce1478c610` |
| `src/SeedLab.Runtime/Hardware/HardwareProbe.cs` | 10,243 | `85ff327834953f62c283a81e538803052136d04e368fba936c5718c82ec090e7` |
| `src/SeedLab.Runtime/Hardware/VolumeInfo.cs` | 5,515 | `86be785f435c7abd5f4b0dd0d8cb6b8b0efff3b68a3c2c0ef90f20e196aefd82` |
| `src/SeedLab.Runtime/RuntimeContext.cs` | 18,932 | `eb67c631636f0af5e051104cc58da930bac0051ca40e37a8a32a1295b781bcd4` |
| `src/SeedLab.Runtime/SeedLab.Runtime.csproj` | 1,564 | `8ec74e49f34d7e1b30c22315fa4736f8b206aca04717a8185ffa53e67f5ea6ca` |
| `src/SeedLab.Runtime/SelfTest/MachineSelfTest.cs` | 12,436 | `63473cf80f28194fbf1d33bf19fc3a95e7b6773d454657318c88e90e230777b2` |
| `src/SeedLab.Runtime/SelfTest/NumericCases.cs` | 11,697 | `4c223069353dd2f035c998e4842948c8a53d84d89d619a53c2eab619d9d22113` |
| `src/SeedLab.Runtime/SelfTest/NumericVectors.cs` | 7,769 | `f8da83565743dbfb1ee85740b917431bab2d626784d35063c9a8301cf6ef23e4` |
| `src/SeedLab.Runtime/SelfTest/SelfTestSuite.cs` | 4,684 | `3597e17b38392d97747db834982cebd63a5e461007cc23e6bb0c35f23cdbf96d` |
| `src/SeedLab.Runtime/SelfTest/selftest-vectors.txt` | 16,194 | `57dc51b7a41cc9b62cabdee259b9920e9a7bb5f3c412eebe0b24bd8200056822` |
| `src/SeedLab.Runtime/Storage/AccessCheck.cs` | 15,416 | `fa58971e456289374f3626eacba6deb010362e9c0fbb622afac2cc0ebdca254f` |
| `src/SeedLab.Runtime/Storage/CacheRoot.cs` | 10,385 | `c22786f503a984d98a1b813831dc75616d8c39b1f77481db71701f31934f3eff` |
| `src/SeedLab.Runtime/Storage/DiskUsageReport.cs` | 4,695 | `afbf73cd9bc5fdbc478cd07882bc611f75adbda07dd181bb31df704ecc837ce0` |
| `src/SeedLab.Runtime/Storage/DurableWrite.cs` | 9,241 | `2a7bb7a52dedbd6f0ea7b45dd65fdf45e6d68bdd52383455085d4ede8673b64e` |
| `src/SeedLab.Runtime/Storage/FileDiagnosis.cs` | 12,287 | `e3e6ad55ac73a81ec6608ebf7c254af8e4d8b13655bb304db7c97d8835e13d1a` |
| `src/SeedLab.Runtime/Storage/FileRetry.cs` | 20,643 | `a7eba58cbc7326be392fc4e738e7467fa89e2a056603edee975ac74d9a7f1e38` |
| `src/SeedLab.Runtime/Storage/ProcessLiveness.cs` | 2,737 | `8ca3977f8673ce4ee20849d3326ab33a1c73875df329e0746f1a83055e7f4cdc` |
| `src/SeedLab.Runtime/Storage/ScratchDirectory.cs` | 5,419 | `aa41c905140ecc6e9f6f0bb9228be1a7c95e961362cdf66130cc7e37c8cbcced` |
| `src/SeedLab.Runtime/Storage/SessionLog.cs` | 12,163 | `818104b5e5956173ddb09f54c3cb9927ead697cb087f392666d373eeca4d6754` |
| `src/SeedLab.Runtime/Storage/SharedRead.cs` | 1,968 | `346d3c557be08957f109c201b58e86aa9dc988d6ae5ec27ac1917a0a03dab8fe` |
| `src/SeedLab.Runtime/Throttle/AutoThrottle.cs` | 6,514 | `fbbdad1db910f4e7ad2e28761a8e28172ebeed58733f97a7b42731b2210cbf8d` |
| `src/SeedLab.Runtime/Throttle/GameWatch.cs` | 5,052 | `839566a8b1b1f1bf94cd2dab980ec54f750c66a8a8e766b943f852cdae7350c7` |
| `src/SeedLab.Saves/Half16.cs` | 6,815 | `811b951266a67f13846720a21c49f809a668b8e11b1c4781e543ead1efe6c5c9` |
| `src/SeedLab.Seeds/LaneTables.cs` | 20,238 | `aaa4cb268f5b3919632f50c2f4946a918bff3a24ec30358a53a673f7e9b813fe` |
| `src/SeedLab.Seeds/SeedAlphabet.cs` | 5,590 | `f241299a42a71f1007d2f38fe6226d094704daa5290e8eabd2c4e82da772277e` |
| `src/SeedLab.Seeds/SeedLab.Seeds.csproj` | 252 | `5f47443a0de0e4eefc441b74cb470f3acb14752d36d19f689729b38a2d636dcc` |
| `src/SeedLab.Seeds/SeedSpace.cs` | 12,050 | `61b2c178056d6b1521b5fb80ad91c17be67e8972f990581b3b72e59be507cc30` |
| `src/SeedLab.Seeds/SeedText.cs` | 13,084 | `004c300bc43a32d78120f3fc03e86d7d5e8ebf77c8d5d4ca53292c7dcac14a2a` |
| `src/SeedLab.Seeds/StableHash.cs` | 6,671 | `413c8e42222b25c0350ec4426c9d7a47f56e865c61a71df34c567e7eb1a6ba35` |
| `src/SeedLab.WorldGen/Biome.cs` | 7,143 | `d4258f3a859d8349769e1cb7f49b0b1e7da0c2fbb64025bd583a3990ceb200e0` |
| `src/SeedLab.WorldGen/DUtils.cs` | 8,720 | `55f7a5867b920e5c35a4dbf0604815cb4ca9a8f3d1006b12da7f8e453dd7ee49` |
| `src/SeedLab.WorldGen/Noise/FastNoisePort.cs` | 9,809 | `81965434f36f381c0bebc5fa6994bbb0e02a4eac7d06d032306bb38b4e9019bc` |
| `src/SeedLab.WorldGen/Noise/FastNoiseTables.cs` | 14,606 | `9c82e38f53d3905f853a19f896005546c31e872ae9a2a845f4fad9ddb8d91c07` |
| `src/SeedLab.WorldGen/SeedLab.WorldGen.csproj` | 263 | `77d1840a006a5e3dc81901e8a96ca6fc9104349dbdcc6719b30092b1fdc2826b` |
| `src/SeedLab.WorldGen/Unity/PerlinFast.cs` | 16,130 | `e88ac488e521b3acf1a472393bfd3ecf061cf897cd16ffbd5594ac03d9db83fe` |
| `src/SeedLab.WorldGen/Unity/PerlinSelfTest.cs` | 12,556 | `dee54c3b87786964e3179811f8d652c9238a1f82bab3ac4be73981168c021558` |
| `src/SeedLab.WorldGen/Unity/UnityMath.cs` | 13,778 | `75d83ca810a8ce945e5cdd54dee19d1e3672769a1cd93af2d61998162900ec43` |
| `src/SeedLab.WorldGen/Unity/UnityPerlin.cs` | 8,286 | `d84d0a68e2b4ca221ecd43aecf629810ec12c36a6d8c69fb90a2deda4907593d` |
| `src/SeedLab.WorldGen/Unity/UnityRandom.cs` | 11,012 | `4199ae4e6a36a42c7914d1af2fa1f0b32cf640ece7d39c4f701350e115de9628` |
| `src/SeedLab.WorldGen/WorldGenTuning.cs` | 1,269 | `9fd1b23ea0d2214bd169acfbbc68ee8d5c1dc1651701c977d6fd7c5c12e49518` |
| `src/SeedLab.WorldGen/WorldGeneratorPort.cs` | 122,956 | `47843ebe1615ca072f52d5706c44f0192f1b48caef02c319ef9bfbece6c2e2dc` |
| `tests/SeedLab.Tests/NativesGoldens.cs` | 44,907 | `5fa6813e360020ffe6b35682f21b7b34fc14ff4dd71cb8bfb068855d702e7a69` |

## Checking it

In a clone of the SeedLab repository that has the commit:

```
git archive 11aeb8f src/SeedLab.WorldGen src/SeedLab.Seeds src/SeedLab.Runtime src/SeedLab.Saves/Half16.cs src/SeedLab.Cli/Infra/NativesSuite.cs tests/SeedLab.Tests/NativesGoldens.cs | tar -x -C <empty folder>
```

Every file under `vendor/` must be identical to the file of the same path in that folder; the archive
has one file more, `src/SeedLab.Runtime/README.md`.

## Updating it

Take the files again from a newer SeedLab commit the same way, update the commit and the table above,
rebuild with `tools/build-package.ps1 -MakeReference` on the reference machine (the fingerprints are
a function of the vendored generator), and raise the program's version.
