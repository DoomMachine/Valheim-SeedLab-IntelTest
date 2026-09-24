using System;

namespace SeedLab.Seeds
{
    /// <summary>
    /// Valheim's string hash, and the rule that turns a seed text into a world's int32 seed.
    ///
    /// Source: <c>StringExtensionMethods.GetStableHashCode</c> (assembly_utils.dll), decompiled
    /// verbatim at decomp/StringExtensionMethods.cs:62-76 and quoted in spec 06 section 1:
    ///
    /// <code>
    /// public static int GetStableHashCode(this string str)
    /// {
    ///     int num = 5381;
    ///     int num2 = num;
    ///     for (int i = 0; i &lt; str.Length &amp;&amp; str[i] != 0; i += 2)
    ///     {
    ///         num = ((num &lt;&lt; 5) + num) ^ str[i];
    ///         if (i == str.Length - 1 || str[i + 1] == '\0')
    ///         {
    ///             break;
    ///         }
    ///         num2 = ((num2 &lt;&lt; 5) + num2) ^ str[i + 1];
    ///     }
    ///     return num + num2 * 1566083941;
    /// }
    /// </code>
    ///
    /// The IL uses plain <c>add</c> / <c>mul</c> / <c>shl</c>, never the <c>.ovf</c> forms, so every
    /// operation wraps at 32 bits (spec 06 section 1, re-verified from the assembly by the reviewer).
    /// The <c>unchecked</c> blocks below make that explicit rather than relying on the project's
    /// default overflow setting.
    ///
    /// The same function names every prefab in the game, so this type is also what hashes
    /// location prefab names when reading a .db2.
    /// </summary>
    public static class StableHash
    {
        /// <summary>Both lanes start here. <c>int num = 5381</c>.</summary>
        public const int LaneSeed = 5381;

        /// <summary>The constant the odd lane is multiplied by before the lanes are added.</summary>
        public const int LaneCombiner = 1566083941;        // 0x5D588B65

        /// <summary>The lane step multiplier: <c>(h &lt;&lt; 5) + h == 33 * h</c>.</summary>
        internal const uint LaneMultiplier = 33u;

        /// <summary>
        /// 33^-1 mod 2^32 (spec 06 section 1: <c>33 * 1041204193 = 1 mod 2^32</c>). Used to walk the
        /// lane step backwards: <c>v = (33*h) ^ c</c> implies <c>h = (v ^ c) * 1041204193</c>.
        /// </summary>
        internal const uint LaneMultiplierInverse = 1041204193u;   // 0x3E0F83E1

        /// <summary>
        /// <c>GetStableHashCode</c> for a string. Null throws, because the game would throw a
        /// <see cref="NullReferenceException"/> on <c>str.Length</c> - it never guards for null.
        /// </summary>
        public static int Compute(string str)
        {
            if (str is null)
            {
                throw new ArgumentNullException(nameof(str));
            }

            return Compute(str.AsSpan());
        }

        /// <summary>
        /// <c>GetStableHashCode</c>, transcribed character for character from the decompiled body
        /// quoted on this type. The only change from the game's source is the parameter type:
        /// <c>str.Length</c> and <c>str[i]</c> behave identically on a span, and the arithmetic is
        /// untouched. Allocation-free, so the inverter can re-hash what it is about to return.
        /// </summary>
        public static int Compute(ReadOnlySpan<char> str)
        {
            unchecked
            {
                int num = 5381;
                int num2 = num;
                for (int i = 0; i < str.Length && str[i] != 0; i += 2)
                {
                    num = ((num << 5) + num) ^ str[i];
                    if (i == str.Length - 1 || str[i + 1] == '\0')
                    {
                        break;
                    }
                    num2 = ((num2 << 5) + num2) ^ str[i + 1];
                }
                return num + num2 * 1566083941;
            }
        }

        /// <summary>
        /// The two lanes separately, for the inverse and for tests. Same loop as
        /// <see cref="Compute(ReadOnlySpan{char})"/>; <c>Compute == Even + LaneCombiner * Odd</c>
        /// (spec 06 section 3.1, verified on 200,000 random strings by two independent ports).
        ///
        /// For a text of length L the even lane consumes ceil(L/2) characters and the odd lane
        /// floor(L/2): for odd L the final iteration updates <c>num</c> and breaks on
        /// <c>i == str.Length - 1</c> before touching <c>num2</c>, so L = 1 leaves the odd lane at 5381.
        /// </summary>
        public static (uint Even, uint Odd) ComputeLanes(ReadOnlySpan<char> str)
        {
            unchecked
            {
                int num = 5381;
                int num2 = num;
                for (int i = 0; i < str.Length && str[i] != 0; i += 2)
                {
                    num = ((num << 5) + num) ^ str[i];
                    if (i == str.Length - 1 || str[i + 1] == '\0')
                    {
                        break;
                    }
                    num2 = ((num2 << 5) + num2) ^ str[i + 1];
                }
                return ((uint)num, (uint)num2);
            }
        }

        /// <summary>
        /// The int32 world seed a seed text produces, including the game's empty-text special case.
        ///
        /// Source: <c>World..ctor(string name, string seed)</c> (decomp/World.cs:69-76):
        /// <c>m_seed = ((!(m_seedName == "")) ? m_seedName.GetStableHashCode() : 0);</c>
        ///
        /// So the empty seed box is not "random": it is the specific world with int seed 0, the same
        /// seed the main-menu background world uses. Note "" and "\0" are different worlds - "\0"
        /// hashes to 371857150 and does not take this branch - though the create-world field can
        /// never produce a NUL (spec 06 section 7.2), so a tool must simply never emit one.
        /// </summary>
        public static int SeedFromText(string seedText)
        {
            if (seedText is null)
            {
                throw new ArgumentNullException(nameof(seedText));
            }

            // Verbatim shape of the ternary in World..ctor.
            return (!(seedText == "")) ? Compute(seedText) : 0;
        }

        /// <summary>
        /// One forward lane: <c>h = 5381</c> then <c>h = ((h &lt;&lt; 5) + h) ^ c</c> per character.
        /// This is what <c>num</c> / <c>num2</c> do in <c>GetStableHashCode</c>, in isolation.
        /// </summary>
        internal static uint LaneForward(ReadOnlySpan<char> chars)
        {
            unchecked
            {
                uint h = LaneSeed;
                for (int i = 0; i < chars.Length; i++)
                {
                    h = ((h << 5) + h) ^ chars[i];
                }
                return h;
            }
        }
    }
}
