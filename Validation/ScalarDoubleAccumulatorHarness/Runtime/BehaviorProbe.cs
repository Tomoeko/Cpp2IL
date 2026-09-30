using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ScalarDoubleAccumulatorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var type = typeof(DoubleAccumulator);
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", type.GetMethods(flags).Length + type.GetConstructors(flags).Length },
                    { "fields", type.GetFields(flags).Length },
                    { "pendingType", type.GetField("Pending").FieldType.FullName },
                    { "currentType", type.GetField("Current").FieldType.FullName },
                    { "baselineType", type.GetField("Baseline").FieldType.FullName },
                    { "totalType", type.GetField("Total").FieldType.FullName },
                    { "applyParameters", type.GetMethod("Apply").GetParameters().Length },
                    { "applyReturn", type.GetMethod("Apply").ReturnType.FullName }
                },
                new Dictionary<string, object> { { "kind", "defaults" }, { "state", State(new DoubleAccumulator()) } }
            };
            var samples = new ulong[,]
            {
                { 0x0000000000000000UL, 0x0000000000000000UL, 0x0000000000000000UL },
                { 0x8000000000000000UL, 0x0000000000000000UL, 0x8000000000000000UL },
                { 0x0000000000000000UL, 0x8000000000000000UL, 0x8000000000000000UL },
                { 0x8000000000000000UL, 0x8000000000000000UL, 0x8000000000000000UL },
                { 0x3ff0000000000000UL, 0x0000000000000000UL, 0x4000000000000000UL },
                { 0xbff0000000000000UL, 0x3ff0000000000000UL, 0x4000000000000000UL },
                { 0x7ff8000000000042UL, 0xfff8000000000002UL, 0x0000000000000000UL },
                { 0x7ff8000000000042UL, 0x0000000000000000UL, 0x7ff8123456789abcUL },
                { 0x0000000000000001UL, 0x0000000000000000UL, 0x0000000000000000UL },
                { 0x8000000000000001UL, 0x0000000000000000UL, 0x8000000000000000UL },
                { 0x000fffffffffffffUL, 0x8000000000000001UL, 0x0000000000000000UL },
                { 0x0010000000000000UL, 0x0000000000000001UL, 0x0000000000000000UL },
                { 0x0010000000000000UL, 0x000fffffffffffffUL, 0x0000000000000000UL },
                { 0x000fffffffffffffUL, 0x000fffffffffffffUL, 0x8000000000000001UL },
                { 0x0000000000000001UL, 0x8000000000000001UL, 0x8000000000000001UL },
                { 0x8010000000000000UL, 0x800fffffffffffffUL, 0x0000000000000000UL },
                { 0x0010000000000000UL, 0x0000000000000000UL, 0x800fffffffffffffUL },
                { 0x4340000000000000UL, 0xbff0000000000000UL, 0xc340000000000000UL },
                { 0x4340000000000000UL, 0xc008000000000000UL, 0xc340000000000000UL },
                { 0x4340000000000000UL, 0x3ff0000000000000UL, 0xc340000000000000UL },
                { 0x3ff0000000000000UL, 0xbca0000000000000UL, 0xbff0000000000000UL },
                { 0x3ff0000000000001UL, 0xbca0000000000000UL, 0xbff0000000000000UL },
                { 0x3ff0000000000000UL, 0x0000000000000000UL, 0x3ca0000000000000UL },
                { 0x3ff0000000000001UL, 0x0000000000000000UL, 0x3ca0000000000000UL },
                { 0x7fefffffffffffffUL, 0xffefffffffffffffUL, 0xffefffffffffffffUL },
                { 0x7ff0000000000000UL, 0x3ff0000000000000UL, 0xfff0000000000000UL },
                { 0x7ff0000000000000UL, 0x7ff0000000000000UL, 0x0000000000000000UL },
                { 0xfff0000000000000UL, 0x3ff0000000000000UL, 0x3ff0000000000000UL },
                { 0x3ff0000000000000UL, 0xfff0000000000000UL, 0x3ff0000000000000UL },
                { 0x7ff8000000000042UL, 0x3ff0000000000000UL, 0x0000000000000000UL },
                { 0x3ff0000000000000UL, 0xfff8000000000002UL, 0x0000000000000000UL },
                { 0x3ff0000000000000UL, 0x0000000000000000UL, 0x7ff8123456789abcUL },
            };
            for (var index = 0; index < samples.GetLength(0); index++)
            {
                var owner = new DoubleAccumulator
                {
                    Current = FromBits(samples[index, 0]), Baseline = FromBits(samples[index, 1]),
                    Total = FromBits(samples[index, 2])
                };
                Record(rows, "disabled", index, owner, false, samples);
                owner.Pending = true;
                Record(rows, "enabled", index, owner, false, samples);
                var alias = owner;
                Record(rows, "repeat-alias", index, alias, ReferenceEquals(owner, alias), samples);
            }
            var exception = "none";
            try { Invoke(null); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object> { { "kind", "null-owner" }, { "exception", exception }, { "state", null } });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-double-accumulator" }, { "observations", rows }
            }));
        }

        private static double FromBits(ulong bits)
        {
            return BitConverter.Int64BitsToDouble(unchecked((long)bits));
        }

        private static string Bits(double value)
        {
            return unchecked((ulong)BitConverter.DoubleToInt64Bits(value)).ToString("x16");
        }

        private static object State(DoubleAccumulator owner)
        {
            return new Dictionary<string, object>
            {
                { "pending", owner.Pending }, { "currentBits", Bits(owner.Current) },
                { "baselineBits", Bits(owner.Baseline) }, { "totalBits", Bits(owner.Total) }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(DoubleAccumulator owner)
        {
            owner.Apply();
        }

        private static void Record(List<object> rows, string kind, int sample, DoubleAccumulator owner,
            bool alias, ulong[,] samples)
        {
            var exception = "none";
            try { Invoke(owner); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "sample", sample }, { "alias", alias }, { "exception", exception },
                { "currentInput", samples[sample, 0].ToString("x16") },
                { "baselineInput", samples[sample, 1].ToString("x16") },
                { "totalInput", samples[sample, 2].ToString("x16") }, { "state", State(owner) }
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
