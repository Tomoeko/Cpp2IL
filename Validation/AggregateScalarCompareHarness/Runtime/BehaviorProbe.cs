using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AggregateScalarCompareFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var bits = new uint[] { 0x00000000, 0x80000000, 0x3f800000, 0xbf800000, 0x00000001,
                0x80000001, 0x00800000, 0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000,
                0x7fc00001, 0xffc00002 };
            var comparer = new ScalarPairComparer();
            var observations = new List<object>();
            foreach (var kind in new[] { "static-low", "static-high", "instance-low", "instance-high" })
            for (var left = 0; left < bits.Length; left++)
            for (var right = 0; right < bits.Length; right++)
            {
                var low = kind.EndsWith("low", StringComparison.Ordinal);
                var leftOther = bits[(left + 3) % bits.Length];
                var rightOther = bits[(right + 5) % bits.Length];
                var first = new ScalarPair { Low = Float(low ? bits[left] : leftOther), High = Float(low ? leftOther : bits[left]) };
                var second = new ScalarPair { Low = Float(low ? bits[right] : rightOther), High = Float(low ? rightOther : bits[right]) };
                bool result;
                switch (kind)
                {
                    case "static-low": result = ScalarPairComparer.StaticLowGreater(first, second); break;
                    case "static-high": result = ScalarPairComparer.StaticHighGreater(first, second); break;
                    case "instance-low": result = comparer.InstanceLowGreater(first, second); break;
                    default: result = comparer.InstanceHighGreater(first, second); break;
                }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", kind }, { "leftBits", Hex(bits[left]) }, { "rightBits", Hex(bits[right]) },
                    { "leftOtherBits", Hex(leftOther) }, { "rightOtherBits", Hex(rightOther) },
                    { "result", result }, { "firstLowAfter", Hex(Bits(first.Low)) },
                    { "firstHighAfter", Hex(Bits(first.High)) }, { "secondLowAfter", Hex(Bits(second.Low)) },
                    { "secondHighAfter", Hex(Bits(second.High)) }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "aggregate-scalar-compare" }, { "observations", observations }
            }));
        }

        private static float Float(uint bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
        private static uint Bits(float value) { return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0); }
        private static string Hex(uint bits) { return bits.ToString("x8", CultureInfo.InvariantCulture); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try
                {
                    Write(arguments[index + 1], "player");
                    Application.Quit(0);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
