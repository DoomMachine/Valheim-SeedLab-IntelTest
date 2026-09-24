namespace SeedLab.WorldGen
{
    /// <summary>
    /// Switches that select between two implementations that must produce identical output. They exist
    /// so a test can run both and compare, not so a user can tune anything: every setting here is
    /// required to make no difference to a single bit of any world, and the point of the switch is to
    /// let that claim be checked rather than asserted.
    ///
    /// <para><b>These are process-wide and are NOT part of the world's identity.</b> Nothing here
    /// carries seed data, and flipping one mid-run does not corrupt an existing handle - a handle reads
    /// the flag only while it is pre-generating. Leave them at their defaults outside a test.</para>
    /// </summary>
    public static class WorldGenTuning
    {
        /// <summary>
        /// O4. When true, <c>MergePoints</c> uses the game's literal O(n^2) scan instead of the
        /// bucket-grid index that replaces it. Default false (the fast path). The two are compared
        /// exhaustively over whole worlds - lakes, rivers, streams and every river point - and must be
        /// bit-identical; see <c>MergePointsBuckets</c> for why they have to be.
        /// </summary>
        public static bool LegacyMergePoints;
    }
}
