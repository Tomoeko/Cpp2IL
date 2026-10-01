using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeScalarDoubleLeafFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly ulong[,] SumSamples =
        {
            { 0, 0 }, { 0x8000000000000000UL, 0 }, { 0, 0x8000000000000000UL },
            { 0x8000000000000000UL, 0x8000000000000000UL },
            { 0x3ff0000000000000UL, 0x4000000000000000UL },
            { 0x3ff0000000000000UL, 0xbff0000000000000UL },
            { 1, 1 }, { 0x8000000000000001UL, 1 }, { 0x000fffffffffffffUL, 1 },
            { 0x0010000000000000UL, 0x8000000000000001UL },
            { 0x4340000000000000UL, 0x3ff0000000000000UL },
            { 0x4340000000000001UL, 0x3ff0000000000000UL },
            { 0x7fefffffffffffffUL, 0x7fefffffffffffffUL },
            { 0xffefffffffffffffUL, 0xffefffffffffffffUL },
            { 0x7ff0000000000000UL, 0x3ff0000000000000UL },
            { 0xfff0000000000000UL, 0xbff0000000000000UL },
            { 0x7ff0000000000000UL, 0xfff0000000000000UL },
            { 0x7ff8000000000042UL, 0x3ff0000000000000UL },
            { 0x3ff0000000000000UL, 0xfff8000000000002UL }
        };

        private static readonly long[] LongSamples =
        {
            0, 1, -1, 3, -3,
            9007199254740991L, 9007199254740992L, 9007199254740993L,
            9007199254740994L, 9007199254740995L,
            -9007199254740991L, -9007199254740992L, -9007199254740993L,
            -9007199254740994L, -9007199254740995L,
            18014398509481983L, 18014398509481985L, 18014398509481987L,
            long.MaxValue, long.MaxValue - 1, long.MinValue, long.MinValue + 1
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static |
                                       BindingFlags.DeclaredOnly;
            var fields = typeof(DoubleFields);
            var conversions = typeof(DoubleConversions);
            var sum = fields.GetMethod("Sum");
            var conversion = conversions.GetMethod("Eighth");
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", fields.GetMethods(flags).Length + fields.GetConstructors(flags).Length +
                                 conversions.GetMethods(flags).Length + conversions.GetConstructors(flags).Length +
                                 (fields.TypeInitializer == null ? 0 : 1) +
                                 (conversions.TypeInitializer == null ? 0 : 1) },
                    { "fields", fields.GetFields(flags).Length + conversions.GetFields(flags).Length },
                    { "types", 2 }, { "sumReturn", sum.ReturnType.FullName },
                    { "sumParameters", sum.GetParameters().Length },
                    { "firstType", fields.GetField("First").FieldType.FullName },
                    { "secondType", fields.GetField("Second").FieldType.FullName },
                    { "conversionParameter", conversion.GetParameters()[0].ParameterType.FullName },
                    { "conversionReturn", conversion.ReturnType.FullName },
                    { "conversionStatic", conversion.IsStatic },
                    { "markersReadonly", fields.GetField("Marker").IsInitOnly &&
                                         conversions.GetField("Marker").IsInitOnly },
                    { "typeInitializers", (fields.TypeInitializer == null ? 0 : 1) +
                                          (conversions.TypeInitializer == null ? 0 : 1) }
                }
            };
            var defaults = new DoubleFields();
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "defaults" }, { "firstBits", Bits(defaults.First) },
                { "secondBits", Bits(defaults.Second) }, { "fieldsMarker", DoubleFields.Marker },
                { "conversionsMarker", DoubleConversions.Marker }
            });
            for (var index = 0; index < SumSamples.GetLength(0); index++)
            {
                var owner = new DoubleFields
                {
                    First = BitConverter.Int64BitsToDouble(unchecked((long)SumSamples[index, 0])),
                    Second = BitConverter.Int64BitsToDouble(unchecked((long)SumSamples[index, 1]))
                };
                RecordSum(rows, "sum", index, owner, false);
                var alias = owner;
                RecordSum(rows, "repeat-alias", index, alias, ReferenceEquals(owner, alias));
            }
            var exception = "none";
            try { InvokeSum(null); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object> { { "kind", "null-owner" }, { "exception", exception } });
            foreach (var value in LongSamples)
            {
                RecordConversion(rows, "Eighth", value);
                RecordConversion(rows, "EighthAlias", value);
                RecordConversion(rows, "Tenth", value);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-scalar-double-leaf" }, { "observations", rows }
            }));
        }

        private static string Bits(double value)
        {
            return unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double InvokeSum(DoubleFields owner)
        {
            return owner.Sum();
        }

        private static void RecordSum(List<object> rows, string kind, int sample, DoubleFields owner, bool alias)
        {
            double result = 0;
            var exception = "none";
            try { result = InvokeSum(owner); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "sample", sample }, { "alias", alias }, { "exception", exception },
                { "firstBits", Bits(owner.First) }, { "secondBits", Bits(owner.Second) },
                { "resultBits", Bits(result) }
            });
        }

        private static void RecordConversion(List<object> rows, string method, long value)
        {
            double result = 0;
            var exception = "none";
            try
            {
                switch (method)
                {
                    case "Eighth": result = DoubleConversions.Eighth(value); break;
                    case "EighthAlias": result = DoubleConversions.EighthAlias(value); break;
                    case "Tenth": result = DoubleConversions.Tenth(value); break;
                    default: throw new ArgumentException("Unknown conversion operation");
                }
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "conversion" }, { "method", method },
                { "input", value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                { "exception", exception }, { "resultBits", Bits(result) },
                { "fieldsMarker", DoubleFields.Marker }, { "conversionsMarker", DoubleConversions.Marker }
            });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
