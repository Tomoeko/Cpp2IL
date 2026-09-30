using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ScalarFloatConversionFixture;
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
                0x00800000, 0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000, 0x7fc00001,
                0xffc00002, 0x7f800001, 0xff800002, 0x007fffff, 0x3f000000
            };
            foreach (var bits in bits32)
            {
                var value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                observations.Add(Row(32, bits, Bits(value), Bits(ScalarConversions.Widen(value))));
            }
            var bits64 = new ulong[]
            {
                0x0000000000000000UL, 0x8000000000000000UL, 0x3ff0000000000000UL, 0xbff0000000000000UL,
                0x0000000000000001UL, 0x8000000000000001UL, 0x0010000000000000UL, 0x7fefffffffffffffUL,
                0xffefffffffffffffUL, 0x7ff0000000000000UL, 0xfff0000000000000UL, 0x7ff8000000000001UL,
                0xfff8000000000002UL, 0x7ff0000000000001UL, 0xfff0000000000002UL,
                0x3ff0000010000000UL, 0x3ff0000010000001UL, 0x3ff0000030000000UL, 0xbff0000010000000UL,
                0x47efffffe0000000UL, 0x47effffff0000000UL, 0x47efffffefffffffUL,
                0x3810000000000000UL, 0x380fffffc0000000UL, 0x3690000000000000UL,
                0x3690000000000001UL, 0x380ffffff0000000UL, 0x7ff8123456789abcUL, 0xfff0123456789abcUL
            };
            foreach (var bits in bits64)
            {
                var value = BitConverter.ToDouble(BitConverter.GetBytes(bits), 0);
                observations.Add(Row(64, bits, Bits(value), Bits(ScalarConversions.Narrow(value))));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-float-conversion" }, { "observations", observations }
            }));
        }

        private static ulong Bits(float value) { return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0); }
        private static ulong Bits(double value) { return BitConverter.ToUInt64(BitConverter.GetBytes(value), 0); }

        private static object Row(int sourceWidth, ulong input, ulong argument, ulong result)
        {
            var inputFormat = sourceWidth == 32 ? "x8" : "x16";
            return new Dictionary<string, object>
            {
                { "sourceWidth", sourceWidth }, { "inputBits", input.ToString(inputFormat, CultureInfo.InvariantCulture) },
                { "argumentBits", argument.ToString(inputFormat, CultureInfo.InvariantCulture) },
                { "resultBits", result.ToString(sourceWidth == 32 ? "x16" : "x8", CultureInfo.InvariantCulture) }
            };
        }

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
