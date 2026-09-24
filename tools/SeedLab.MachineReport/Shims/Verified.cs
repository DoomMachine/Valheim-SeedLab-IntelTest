namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// Stands in for SeedLab.Cli's <c>Verified</c> class, which the vendored <c>NativesSuite.cs</c>
    /// calls to find the ground-truth folder. SeedLab's own class also carries the game build stamp
    /// and probes for a Valheim install; none of that belongs in this package, so only the one member
    /// the suite uses is provided. It answers with the package folder, whose <c>natives\</c>
    /// subfolder is where <see cref="NativesGoldenSuite"/> then looks - and the caller checks
    /// <see cref="NativesGoldenSuite.Directory_"/> to prove that is the folder it used.
    /// </summary>
    public static class Verified
    {
        public static string? FindGroundTruth() => SeedLab.MachineReport.Package.Root;
    }
}
