using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BooleanCompositionFixture;
using UnityEngine;

namespace RecoveryValidation
{
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
                var state = new CompositionState { Single = right, Updates = 17 };
                state.RetainGreater32(left, right);
                observations.Add(Observe(32, leftBits, rightBits,
                    BitConverter.ToUInt32(BitConverter.GetBytes(state.Single), 0), state.Updates));
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
                var state = new CompositionState { Double = right, Updates = 17 };
                state.RetainGreater64(left, right);
                observations.Add(Observe(64, leftBits, rightBits,
                    BitConverter.ToUInt64(BitConverter.GetBytes(state.Double), 0), state.Updates));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "boolean-composition" }, { "observations", observations }
            }));
        }

        private static object Observe(int width, ulong left, ulong right, ulong result, int updates)
        {
            var format = width == 32 ? "x8" : "x16";
            return new Dictionary<string, object>
            {
                { "width", width }, { "leftBits", left.ToString(format, CultureInfo.InvariantCulture) },
                { "rightBits", right.ToString(format, CultureInfo.InvariantCulture) },
                { "resultBits", result.ToString(format, CultureInfo.InvariantCulture) }, { "updates", updates }
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
