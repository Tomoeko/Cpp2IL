using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using IntegerExtensionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var value = 0; value < 256; value++)
            {
                var signed = unchecked((sbyte)value);
                var unsigned = (byte)value;
                observations.Add(new Dictionary<string, object>
                {
                    { "width", 8 }, { "inputBits", Hex((ulong)value, 8) },
                    { "sign32", Hex(unchecked((uint)IntegerExtensions.Sign8To32(signed)), 32) },
                    { "zero32", Hex((uint)IntegerExtensions.Zero8To32(unsigned), 32) },
                    { "sign64", Hex(unchecked((ulong)IntegerExtensions.Sign8To64(signed)), 64) },
                    { "zero64", Hex(IntegerExtensions.Zero8To64(unsigned), 64) },
                    { "signU32", Hex(IntegerExtensions.Sign8ToU32(signed), 32) }
                });
            }
            var words = new ushort[] { 0, 1, 0x7f, 0x80, 0xff, 0x100, 0x7fff, 0x8000, 0x8001, 0xff00, 0xff7f, 0xffff };
            foreach (var value in words)
            {
                var signed = unchecked((short)value);
                observations.Add(new Dictionary<string, object>
                {
                    { "width", 16 }, { "inputBits", Hex(value, 16) },
                    { "sign32", Hex(unchecked((uint)IntegerExtensions.Sign16To32(signed)), 32) },
                    { "zero32", Hex((uint)IntegerExtensions.Zero16To32(value), 32) },
                    { "sign64", Hex(unchecked((ulong)IntegerExtensions.Sign16To64(signed)), 64) },
                    { "zero64", Hex(IntegerExtensions.Zero16To64(value), 64) }
                });
            }
            var dwords = new uint[] { 0, 1, 0x7fff, 0x8000, 0xffff, 0x10000, 0x7fffffff, 0x80000000,
                0x80000001, 0xffff0000, 0xfffffffe, 0xffffffff };
            foreach (var value in dwords)
            {
                var signed = unchecked((int)value);
                observations.Add(new Dictionary<string, object>
                {
                    { "width", 32 }, { "inputBits", Hex(value, 32) },
                    { "sign64", Hex(unchecked((ulong)IntegerExtensions.Sign32To64(signed)), 64) },
                    { "zero64", Hex(IntegerExtensions.Zero32To64(value), 64) },
                    { "signU64", Hex(IntegerExtensions.Sign32ToU64(signed), 64) }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "integer-extensions" }, { "observations", observations }
            }));
        }

        private static string Hex(ulong value, int width)
        {
            return value.ToString("x" + (width / 4), CultureInfo.InvariantCulture);
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
