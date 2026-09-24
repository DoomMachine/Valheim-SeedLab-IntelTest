using System;

namespace SeedLab.WorldGen
{
    /// <summary>
    /// Heightmap.Biome (assembly_valheim, decompiled) - a flags enum, values verified with Mono.Cecil.
    /// The game's own dense index is <see cref="BiomeIndex"/>; see <see cref="BiomeExtensions"/>.
    /// </summary>
    [Flags]
    public enum Biome
    {
        None = 0,
        Meadows = 1,
        Swamp = 2,
        Mountain = 4,
        BlackForest = 8,
        Plains = 16,
        AshLands = 32,
        DeepNorth = 64,
        Ocean = 256,
        Mistlands = 512,
        All = 895,
        Land = 639,
    }

    /// <summary>
    /// Heightmap.BiomeIndex, byte for byte (decomp/Heightmap.cs 32-45). <b>None is 0 and Meadows is 1</b>,
    /// so this is one higher than SeedLab's own packing - see
    /// <see cref="BiomeExtensions.ToDenseIndex"/>. Anything that has to speak the game's index -
    /// AltBiomeWorldData.PointBiomes (decomp/AltBiomeWorldData.cs 32, 115, 121), the BiomeSector
    /// neighbour loop (decomp/BiomeSector.cs 142, 165), Heightmap.GetBiome's corner blend
    /// (decomp/Heightmap.cs 490-508, which scans j = 1..9 over a float[10]) or a biome byte read out of
    /// a .db2 chunk or a spec-04 dump - must use THIS enum, never the dense one.
    /// </summary>
    public enum BiomeIndex : byte
    {
        None = 0,
        Meadows = 1,
        Swamp = 2,
        Mountain = 3,
        BlackForest = 4,
        Plains = 5,
        AshLands = 6,
        DeepNorth = 7,
        Ocean = 8,
        Mistlands = 9,
        Count = 10,
    }

    public static class BiomeExtensions
    {
        /// <summary>
        /// BiomeHelpers.ToBiomeIndex(this Heightmap.Biome) - the game's own mapping, decompiled
        /// (reproduce with tools\decompile.ps1 -Type BiomeHelpers). It is a switch over
        /// the nine single-bit values that <b>throws NotImplementedException</b> for anything else
        /// (a combined mask such as Biome.Land, or an undefined bit), and that throw is reproduced here
        /// rather than smoothed over, because silently returning a sentinel is how the dense-index trap
        /// below was born.
        /// </summary>
        public static BiomeIndex ToBiomeIndex(this Biome b) => b switch
        {
            Biome.None => BiomeIndex.None,
            Biome.Meadows => BiomeIndex.Meadows,
            Biome.Swamp => BiomeIndex.Swamp,
            Biome.Mountain => BiomeIndex.Mountain,
            Biome.BlackForest => BiomeIndex.BlackForest,
            Biome.Plains => BiomeIndex.Plains,
            Biome.AshLands => BiomeIndex.AshLands,
            Biome.DeepNorth => BiomeIndex.DeepNorth,
            Biome.Ocean => BiomeIndex.Ocean,
            Biome.Mistlands => BiomeIndex.Mistlands,
            _ => throw new NotImplementedException("Heightmap.Biome " + (int)b + " has no BiomeIndex"),
        };

        /// <summary>
        /// BiomeHelpers.ToBiome(this Heightmap.BiomeIndex) - the inverse, and it throws the same way.
        /// </summary>
        public static Biome ToBiome(this BiomeIndex b) => b switch
        {
            BiomeIndex.None => Biome.None,
            BiomeIndex.Meadows => Biome.Meadows,
            BiomeIndex.Swamp => Biome.Swamp,
            BiomeIndex.Mountain => Biome.Mountain,
            BiomeIndex.BlackForest => Biome.BlackForest,
            BiomeIndex.Plains => Biome.Plains,
            BiomeIndex.AshLands => Biome.AshLands,
            BiomeIndex.DeepNorth => Biome.DeepNorth,
            BiomeIndex.Ocean => Biome.Ocean,
            BiomeIndex.Mistlands => Biome.Mistlands,
            _ => throw new NotImplementedException("BiomeIndex " + (int)b + " has no Biome"),
        };

        /// <summary>
        /// The exact equivalent of the game's <c>BiomeHelpers.ToIndex(this Heightmap.Biome)</c>, which is
        /// just <c>(int)b.ToBiomeIndex()</c>: <b>None = 0, Meadows = 1 ... Mistlands = 9</b>.
        /// Use this - not <see cref="ToDenseIndex"/> - whenever the number will be compared with, or
        /// written next to, a value that came from the game.
        /// </summary>
        public static int ToGameIndex(this Biome b) => (int)b.ToBiomeIndex();

        /// <summary>
        /// <b>SeedLab's own 0-based packing - this is NOT Heightmap.BiomeIndex.</b>
        /// Meadows = 0 ... Mistlands = 8, and everything else (including Biome.None) maps to -1.
        ///
        /// <para>It exists only as an array slot for the nine real biomes and as the byte written into
        /// the <c>.biome.u8</c> ground-truth files by SeedLab.Saves.MinimapCache, which read it back with
        /// the same convention. Do not hand the result to anything game-side: the game's index is one
        /// higher (see <see cref="ToGameIndex"/>). Note also that casting the -1 to a byte yields 255,
        /// which collides with the <c>AmbiguousWhite</c> marker in those same files, so callers must
        /// filter Biome.None themselves.</para>
        /// </summary>
        public static int ToDenseIndex(this Biome b) => b switch
        {
            Biome.Meadows => 0,
            Biome.Swamp => 1,
            Biome.Mountain => 2,
            Biome.BlackForest => 3,
            Biome.Plains => 4,
            Biome.AshLands => 5,
            Biome.DeepNorth => 6,
            Biome.Ocean => 7,
            Biome.Mistlands => 8,
            _ => -1,
        };

        /// <summary>The inverse of <see cref="ToDenseIndex"/>; anything out of range gives Biome.None.</summary>
        public static Biome FromDenseIndex(int i) => i switch
        {
            0 => Biome.Meadows,
            1 => Biome.Swamp,
            2 => Biome.Mountain,
            3 => Biome.BlackForest,
            4 => Biome.Plains,
            5 => Biome.AshLands,
            6 => Biome.DeepNorth,
            7 => Biome.Ocean,
            8 => Biome.Mistlands,
            _ => Biome.None,
        };

        /// <summary>
        /// Former name of <see cref="ToDenseIndex"/>. It was documented as "Heightmap.BiomeIndex", which
        /// it never was - the game has a real <c>BiomeHelpers.ToIndex</c> with the same name and a
        /// DIFFERENT numbering (None = 0, Meadows = 1 ... Mistlands = 9, throwing otherwise). Kept as a
        /// forwarding alias so the existing SeedLab.Saves and test call sites keep their behaviour and
        /// the oracle files keep their bytes; migrate them to <see cref="ToDenseIndex"/>, or to
        /// <see cref="ToGameIndex"/> if what they actually wanted was the game's number.
        /// </summary>
        [Obsolete("Ambiguous: the game's BiomeHelpers.ToIndex is 1-based and throws. Use ToDenseIndex() for SeedLab's own 0-based packing, or ToGameIndex() for the game's Heightmap.BiomeIndex value.")]
        public static int ToIndex(this Biome b) => b.ToDenseIndex();

        /// <summary>Former name of <see cref="FromDenseIndex"/>; see <see cref="ToIndex"/>.</summary>
        [Obsolete("Ambiguous: use FromDenseIndex() for SeedLab's own 0-based packing, or ((BiomeIndex)i).ToBiome() for the game's numbering.")]
        public static Biome FromIndex(int i) => FromDenseIndex(i);
    }
}
