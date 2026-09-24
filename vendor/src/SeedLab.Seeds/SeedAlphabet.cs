using System;

namespace SeedLab.Seeds
{
    /// <summary>
    /// An ordered set of characters a seed text may be built from.
    ///
    /// Two are built in (spec 06 section 3.2):
    ///   A62 - what a user can type into the create-world box,
    ///   A59 - what the game itself suggests, from <c>World.GenerateSeed</c>.
    ///
    /// Instances are immutable and are used as the key of the process-wide
    /// <see cref="LaneTables"/> cache, so a custom alphabet should be created once and reused.
    /// </summary>
    public sealed class SeedAlphabet
    {
        /// <summary>
        /// <c>0-9 A-Z a-z</c>, 62 symbols - the alphanumeric characters a user can type.
        /// The order here decides which text a greedy descent happens to return first; it does not
        /// affect which lengths are reachable, and the uniform sampler does not depend on it.
        /// </summary>
        public const string Alnum62Chars =
            "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

        /// <summary>
        /// The game's own 59-symbol alphabet, character for character the literal in
        /// <c>World.GenerateSeed</c> (decomp/World.cs:163-171):
        /// <code>
        /// text += "abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789"
        ///         [UnityEngine.Random.Range(0, "...".Length)];
        /// </code>
        /// No <c>o</c>, no <c>O</c>, no <c>1</c> - lookalikes are removed. <c>GenerateSeed</c> always
        /// produces exactly 10 characters, drawn uniformly and independently.
        /// </summary>
        public const string GameAlphabet59Chars =
            "abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789";

        /// <summary>A62: the 62 typeable alphanumerics.</summary>
        public static readonly SeedAlphabet Alnum62 =
            new SeedAlphabet("A62", Alnum62Chars, verifiedMaxLaneWeight5: 81);

        /// <summary>A59: the game's own suggestion alphabet (<c>World.GenerateSeed</c>).</summary>
        public static readonly SeedAlphabet GameAlphabet59 =
            new SeedAlphabet("A59", GameAlphabet59Chars, verifiedMaxLaneWeight5: 81);

        private SeedAlphabet(string name, string characters, int? verifiedMaxLaneWeight5)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("An alphabet needs a name.", nameof(name));
            }

            if (string.IsNullOrEmpty(characters))
            {
                throw new ArgumentException("An alphabet needs at least one character.", nameof(characters));
            }

            for (int i = 0; i < characters.Length; i++)
            {
                // A NUL terminates GetStableHashCode's loop (str[i] != 0 and str[i+1] == '\0'), so a
                // text containing one is not a normal preimage at all - spec 06 section 1. It is also
                // unreachable from the create-world field by any of three routes (section 7.2).
                if (characters[i] == '\0')
                {
                    throw new ArgumentException("An alphabet may not contain NUL.", nameof(characters));
                }

                if (characters.IndexOf(characters[i]) != i)
                {
                    throw new ArgumentException(
                        $"Duplicate character '{characters[i]}' in alphabet '{name}'.", nameof(characters));
                }
            }

            Name = name;
            Characters = characters;
            VerifiedMaxLaneWeight5 = verifiedMaxLaneWeight5;
            Key = name + "|" + characters;
        }

        /// <summary>Build a custom alphabet. See <see cref="VerifiedMaxLaneWeight5"/> first.</summary>
        public static SeedAlphabet Custom(string name, string characters, int? verifiedMaxLaneWeight5 = null)
            => new SeedAlphabet(name, characters, verifiedMaxLaneWeight5);

        public string Name { get; }

        public string Characters { get; }

        public int Count => Characters.Length;

        /// <summary>Cache key for <see cref="LaneTables.For"/>.</summary>
        internal string Key { get; }

        /// <summary>
        /// max w_5 for this alphabet, if it has been computed exhaustively - 81 = 3^4 for both A62
        /// and A59 (spec 06 section 3.3, confirmed exhaustively for n = 1..5 by two implementations,
        /// and again by the reviewer in section 11).
        ///
        /// It is the rejection constant of the uniform 10-character sampler. The sampler stays exactly
        /// uniform for ANY sound upper bound - too large only costs probes - so when this is null
        /// <see cref="LaneTables.MaxLaneWeightBound"/> falls back to the sound but loose
        /// <c>Count * max w_4</c>. Never hard-code 81 for a new alphabet: spec 06 section 10 flags
        /// that 81 is an empirical maximum, not a proved bound, and too small a constant silently
        /// breaks uniformity.
        /// </summary>
        public int? VerifiedMaxLaneWeight5 { get; }

        public bool Contains(char c) => Characters.IndexOf(c) >= 0;

        /// <summary>True when every character of <paramref name="text"/> is in this alphabet.</summary>
        public bool CoversText(ReadOnlySpan<char> text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (Characters.IndexOf(text[i]) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        public override string ToString() => $"{Name} ({Count} chars)";
    }
}
