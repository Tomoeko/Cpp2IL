using System;
using System.Collections.Generic;
using System.IO;
using EnumIntegerConversionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static IEnumerable<int> WordBits()
        {
            var seen = new HashSet<int>();
            foreach (var bits in new[] { 0, 1, 2, 127, 128, 255, 256, 32766, 32767, 32768, 32769, 65534, 65535 })
                if (seen.Add(bits)) yield return bits;
            for (var bit = 0; bit < 16; bit++)
                foreach (var bits in new[] { 1 << bit, 65535 ^ (1 << bit) })
                    if (seen.Add(bits)) yield return bits;
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var bits = 0; bits < 256; bits++)
            {
                var signed = unchecked((SByteCode)(sbyte)bits);
                var unsigned = (ByteCode)(byte)bits;
                Record(observations, "sbyte-i4", bits, (int)signed, () => Conversions.SByteToInt32(signed), () => (int)signed);
                Record(observations, "sbyte-u4", bits, (int)signed, () => Conversions.SByteToUInt32(signed), () => (int)signed);
                Record(observations, "byte-i4", bits, (int)unsigned, () => Conversions.ByteToInt32(unsigned), () => (int)unsigned);
                Record(observations, "byte-u4", bits, (int)unsigned, () => Conversions.ByteToUInt32(unsigned), () => (int)unsigned);
            }
            foreach (var bits in WordBits())
            {
                var signed = unchecked((Int16Code)(short)bits);
                var unsigned = (UInt16Code)(ushort)bits;
                Record(observations, "int16-i4", bits, (int)signed, () => Conversions.Int16ToInt32(signed), () => (int)signed);
                Record(observations, "int16-u4", bits, (int)signed, () => Conversions.Int16ToUInt32(signed), () => (int)signed);
                Record(observations, "uint16-i4", bits, (int)unsigned, () => Conversions.UInt16ToInt32(unsigned), () => (int)unsigned);
                Record(observations, "uint16-u4", bits, (int)unsigned, () => Conversions.UInt16ToUInt32(unsigned), () => (int)unsigned);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "enum-integer-conversion" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> rows, string route, int bits, int input, Func<long> action, Func<int> after)
        {
            var failure = "none";
            long? result = null;
            try { result = action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "route", route }, { "bits", bits }, { "input", input }, { "inputAfter", after() },
                { "result", result }, { "failure", failure }
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
