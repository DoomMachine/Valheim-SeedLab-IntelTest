using System;
using System.Collections.Generic;
using System.Globalization;

namespace SeedLab.Runtime.SelfTest
{
    /// <summary>
    /// The arithmetic SeedLab's bit-exactness rests on, reduced to values that can be recorded and
    /// compared bit for bit.
    ///
    /// <para>Two kinds of case:</para>
    /// <list type="bullet">
    /// <item><description><b>libm</b> - <c>Math.Sin/Cos/Tan/Atan/Atan2/Pow/Sqrt/Exp/Log/Floor</c> at
    /// arguments in the ranges world generation actually uses. These are the calls the decisions file
    /// names as the arm64 risk: the runtime forwards them to the platform's own libm, and two libms may
    /// differ in the last bit.</description></item>
    /// <item><description><b>evaluation</b> - float and double expressions whose result changes if the
    /// JIT contracts a multiply-add into an FMA, keeps an intermediate at a wider precision, or rounds a
    /// double to float differently. The <c>chain</c> cases fold thousands of operations into one value,
    /// so a single divergent bit anywhere cannot hide.</description></item>
    /// </list>
    ///
    /// <para>The cases are defined HERE, in code, and only their results are recorded. A vector file can
    /// therefore never drift away from what is actually being computed.</para>
    /// </summary>
    public static class NumericCases
    {
        /// <summary>One libm call: function name, two arguments (b unused for one-argument functions).</summary>
        public readonly struct LibmCase
        {
            public LibmCase(string fn, double a, double b) { Fn = fn; A = a; B = b; }
            public string Fn { get; }
            public double A { get; }
            public double B { get; }

            public string Key => Fn + " " + Hex.Of(A) + " " + Hex.Of(B);

            public double Evaluate() => Fn switch
            {
                "Sin" => Math.Sin(A),
                "Cos" => Math.Cos(A),
                "Tan" => Math.Tan(A),
                "Atan" => Math.Atan(A),
                "Atan2" => Math.Atan2(A, B),
                "Pow" => Math.Pow(A, B),
                "Sqrt" => Math.Sqrt(A),
                "Exp" => Math.Exp(A),
                "Log" => Math.Log(A),
                "Floor" => Math.Floor(A),
                "Ceiling" => Math.Ceiling(A),
                "Round" => Math.Round(A),
                "Abs" => Math.Abs(A),
                _ => throw new InvalidOperationException("Unknown libm case '" + Fn + "'.")
            };
        }

        /// <summary>A named expression whose exact result is recorded.</summary>
        public sealed class ValueCase
        {
            private ValueCase(string name, Func<double>? evalDouble, Func<float>? evalFloat, bool isFloat)
            {
                Name = name; EvalDouble = evalDouble; EvalFloat = evalFloat; IsFloat = isFloat;
            }

            /// <summary>A case whose result is a double. Named factories, because a lambda returning a
            /// float converts to BOTH delegate types and an overload would be ambiguous.</summary>
            public static ValueCase F64(string name, Func<double> f) => new ValueCase(name, f, null, false);

            /// <summary>A case whose result is a float, compared as 32 bits.</summary>
            public static ValueCase F32(string name, Func<float> f) => new ValueCase(name, null, f, true);

            public string Name { get; }
            public bool IsFloat { get; }
            public Func<double>? EvalDouble { get; }
            public Func<float>? EvalFloat { get; }
        }

        /// <summary>
        /// Arguments chosen to cover what world generation feeds these functions: the world angle
        /// (<c>Atan2</c> over the whole circle), the distance and ridge <c>Pow</c> exponents, the
        /// trigonometry in the mountain and river passes, plus the awkward values (denormals, huge
        /// arguments, exact halves) where implementations disagree if they are going to.
        /// </summary>
        public static IReadOnlyList<LibmCase> Libm()
        {
            List<LibmCase> l = new List<LibmCase>();

            double[] angles =
            {
                0.0, 1e-8, 0.1, 0.5, 0.7853981633974483, 1.0, 1.5707963267948966, 2.0,
                3.141592653589793, 3.5, 6.283185307179586, 10.0, 100.0, 1000.0, 123456.789,
                -0.1, -1.0, -3.141592653589793, -1e6, 1e15
            };
            foreach (double a in angles)
            {
                l.Add(new LibmCase("Sin", a, 0));
                l.Add(new LibmCase("Cos", a, 0));
            }
            foreach (double a in new[] { 0.0, 0.3, 1.0, 1.5, 3.0, -0.7, 1e-8, 1e8 })
            {
                l.Add(new LibmCase("Tan", a, 0));
                l.Add(new LibmCase("Atan", a, 0));
            }

            // Atan2 over the circle: this is the world-angle call the profile found being evaluated
            // three times per GetBiome.
            double[] xs = { 0.0, 1.0, -1.0, 1e-7, 10500.0, -10500.0, 7648.0, 0.5, 123.456 };
            double[] zs = { 0.0, 1.0, -1.0, 1e-7, 10500.0, -10500.0, -7648.0, -0.5, 987.654 };
            foreach (double x in xs)
                foreach (double z in zs)
                    l.Add(new LibmCase("Atan2", x, z));

            double[] bases = { 0.5, 1.0, 1.5, 2.0, 10.0, 1.0001, 0.9999, 1234.5 };
            double[] exps = { 0.0, 0.5, 1.0, 1.5, 2.0, 3.0, -1.0, 0.3333333333333333, 1.5707963267948966 };
            foreach (double b in bases)
                foreach (double e in exps)
                    l.Add(new LibmCase("Pow", b, e));

            foreach (double a in new[] { 0.0, 1e-300, 0.5, 1.0, 2.0, 3.0, 1e6, 1e15, 110250000.0 })
            {
                l.Add(new LibmCase("Sqrt", a, 0));
                if (a > 0) l.Add(new LibmCase("Log", a, 0));
            }
            foreach (double a in new[] { -700.0, -1.0, 0.0, 0.5, 1.0, 10.0, 88.0 })
                l.Add(new LibmCase("Exp", a, 0));

            foreach (double a in new[] { -2.5, -0.5, 0.0, 0.5, 1.5, 2.5, 1e15 + 0.5, -1e15 - 0.5 })
            {
                l.Add(new LibmCase("Floor", a, 0));
                l.Add(new LibmCase("Ceiling", a, 0));
                l.Add(new LibmCase("Round", a, 0));
            }
            return l;
        }

        /// <summary>The evaluation cases. Order is irrelevant: each is recorded under its own name.</summary>
        public static IReadOnlyList<ValueCase> Values()
        {
            List<ValueCase> l = new List<ValueCase>
            {
                // A multiply-add that a contracted FMA would round only once. If the JIT ever starts
                // contracting - a new runtime, a new architecture - this value changes.
                ValueCase.F64("fma-double", () =>
                {
                    double a = 0.1, b = 0.2, c = -0.020000000000000004;
                    return a * b + c;
                }),
                ValueCase.F32("fma-float", () =>
                {
                    float a = 0.1f, b = 0.2f, c = -0.020000001f;
                    return a * b + c;
                }),

                // Double -> float rounding, at a tie and either side of it.
                ValueCase.F32("f32-cast-tie", () => (float)1.0000000596046448),
                ValueCase.F32("f32-cast-below", () => (float)0.9999999999999999),
                ValueCase.F32("f32-cast-denormal", () => (float)1.0e-45),

                // float division and square root, where an x87 or a fast-math path would differ.
                ValueCase.F32("f32-div", () => 1.0f / 3.0f),
                ValueCase.F32("f32-sqrt", () => (float)Math.Sqrt(2.0f)),
                ValueCase.F32("f32-of-sin", () => (float)Math.Sin(0.7853981633974483)),

                // The shapes world generation is full of: lerps, smoothsteps and a normalised distance.
                ValueCase.F32("lerp-f32", () =>
                {
                    float a = -1.5f, b = 2.25f, t = 0.37f;
                    return a + (b - a) * t;
                }),
                ValueCase.F32("smoothstep-f32", () =>
                {
                    float t = 0.37f;
                    float c = t * t * (3f - 2f * t);
                    return c;
                }),
                ValueCase.F32("length-f32", () =>
                {
                    float x = 10500f, z = -7648f;
                    return (float)Math.Sqrt(x * x + z * z);
                }),

                // The folded chains. Thousands of operations reduced to one bit pattern: the cheapest
                // possible proof that nothing in the float pipeline differs.
                ValueCase.F32("chain-f32", () =>
                {
                    float acc = 1.0f;
                    uint s = 0x9E3779B9u;
                    for (int i = 0; i < 4096; i++)
                    {
                        s = s * 1664525u + 1013904223u;
                        float x = (s >> 8) * (1.0f / 16777216.0f);
                        acc = acc * 0.5f + x * 1.5f;
                        acc = acc - (float)Math.Floor(acc);
                        acc += (float)Math.Sin(x) * 0.25f;
                        acc = acc / (1.0f + x * 0.125f);
                    }
                    return acc;
                }),
                ValueCase.F64("chain-f64", () =>
                {
                    double acc = 1.0;
                    uint s = 0x85EBCA6Bu;
                    for (int i = 0; i < 4096; i++)
                    {
                        s = s * 1664525u + 1013904223u;
                        double x = (s >> 8) * (1.0 / 16777216.0);
                        acc = acc * 0.5 + x * 1.5;
                        acc = acc - Math.Floor(acc);
                        acc += Math.Atan2(x, 1.0 - x) * 0.25;
                        acc = acc / (1.0 + Math.Pow(x, 1.5) * 0.125);
                    }
                    return acc;
                }),

                // Mixed float/double: the pattern the port uses when the game stores a float and
                // computes in double. A machine that promotes differently shows up here.
                ValueCase.F64("chain-mixed", () =>
                {
                    double acc = 0.0;
                    for (int i = 1; i <= 2048; i++)
                    {
                        float f = (float)(i * 0.001);
                        double d = f;
                        acc += d * d - (double)(f * f);
                        acc = acc * 0.999999 + Math.Sqrt(d);
                    }
                    return acc;
                })
            };
            return l;
        }
    }

    /// <summary>Bit patterns as hex, the only honest way to record a floating-point golden.</summary>
    public static class Hex
    {
        public static string Of(double d) =>
            BitConverter.DoubleToUInt64Bits(d).ToString("X16", CultureInfo.InvariantCulture);

        public static string Of(float f) =>
            BitConverter.SingleToUInt32Bits(f).ToString("X8", CultureInfo.InvariantCulture);

        public static bool TryDouble(string hex, out double value)
        {
            value = 0;
            if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong bits)) return false;
            value = BitConverter.UInt64BitsToDouble(bits);
            return true;
        }

        public static bool TrySingle(string hex, out float value)
        {
            value = 0;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint bits)) return false;
            value = BitConverter.UInt32BitsToSingle(bits);
            return true;
        }
    }
}
