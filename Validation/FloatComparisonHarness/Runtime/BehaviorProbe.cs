using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FloatComparisonFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // Operand construction and observations are independent of the recovery assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var bits32 = new uint[]
            {
                0x00000000, 0x80000000, 0x3f800000, 0xbf800000, 0x00000001, 0x80000001,
                0x00800000, 0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000, 0x7fc00001, 0xffc00002
            };
            foreach (var leftBits in bits32)
            foreach (var rightBits in bits32)
            {
                var left = BitConverter.ToSingle(BitConverter.GetBytes(leftBits), 0);
                var right = BitConverter.ToSingle(BitConverter.GetBytes(rightBits), 0);
                observations.Add(Observe(32, leftBits, rightBits,
                    FloatComparisons.Equal32(left, right), FloatComparisons.NotEqual32(left, right),
                    FloatComparisons.Less32(left, right), FloatComparisons.LessOrEqual32(left, right),
                    FloatComparisons.Greater32(left, right), FloatComparisons.GreaterOrEqual32(left, right)));
            }
            var bits64 = new ulong[]
            {
                0x0000000000000000UL, 0x8000000000000000UL, 0x3ff0000000000000UL, 0xbff0000000000000UL,
                0x0000000000000001UL, 0x8000000000000001UL, 0x0010000000000000UL, 0x7fefffffffffffffUL,
                0xffefffffffffffffUL, 0x7ff0000000000000UL, 0xfff0000000000000UL, 0x7ff8000000000001UL,
                0xfff8000000000002UL
            };
            foreach (var leftBits in bits64)
            foreach (var rightBits in bits64)
            {
                var left = BitConverter.ToDouble(BitConverter.GetBytes(leftBits), 0);
                var right = BitConverter.ToDouble(BitConverter.GetBytes(rightBits), 0);
                observations.Add(Observe(64, leftBits, rightBits,
                    FloatComparisons.Equal64(left, right), FloatComparisons.NotEqual64(left, right),
                    FloatComparisons.Less64(left, right), FloatComparisons.LessOrEqual64(left, right),
                    FloatComparisons.Greater64(left, right), FloatComparisons.GreaterOrEqual64(left, right)));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "float-comparisons" }, { "observations", observations }
            }));
        }

        private static object Observe(int width, ulong left, ulong right, bool equal, bool notEqual,
            bool less, bool lessOrEqual, bool greater, bool greaterOrEqual)
        {
            var format = width == 32 ? "x8" : "x16";
            return new Dictionary<string, object>
            {
                { "width", width }, { "leftBits", left.ToString(format, CultureInfo.InvariantCulture) },
                { "rightBits", right.ToString(format, CultureInfo.InvariantCulture) },
                { "equal", equal }, { "notEqual", notEqual }, { "less", less },
                { "lessOrEqual", lessOrEqual }, { "greater", greater }, { "greaterOrEqual", greaterOrEqual }
            };
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report")
                    continue;
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
