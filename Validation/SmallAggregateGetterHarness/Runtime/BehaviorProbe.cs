using System;
using System.Collections.Generic;
using System.IO;
using SmallAggregateGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Int16Patterns =
        {
            0, 1, 2, 0x7e, 0x7f, 0x80, 0xff, 0x100, 0x101, 0x7ffe, 0x7fff,
            0x8000, 0x8001, 0x80ff, 0xff00, 0xff7f, 0xff80, 0xfffe, 0xffff, 0x1234, 0xabcd
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var bits = 0; bits <= byte.MaxValue; bits++)
            {
                var signed = new SByteValue { Value = unchecked((sbyte)bits) };
                var unsigned = new ByteValue { Value = (byte)bits };
                Record(observations, "readSByte", bits, () => Getters.ReadSByte(signed), () => signed.Value);
                Record(observations, "widenSByte", bits, () => Getters.WidenSByte(signed), () => signed.Value);
                Record(observations, "readByte", bits, () => Getters.ReadByte(unsigned), () => unsigned.Value);
                Record(observations, "widenByte", bits, () => Getters.WidenByte(unsigned), () => unsigned.Value);
            }
            foreach (var bits in Int16Patterns)
            {
                var signed = new Int16Value { Value = unchecked((short)bits) };
                var unsigned = new UInt16Value { Value = (ushort)bits };
                Record(observations, "readInt16", bits, () => (short)signed, () => signed.Value);
                Record(observations, "widenInt16", bits, () => Getters.WidenInt16(signed), () => signed.Value);
                Record(observations, "readUInt16", bits, () => Getters.ReadUInt16(unsigned), () => unsigned.Value);
                Record(observations, "widenUInt16", bits, () => Getters.WidenUInt16(unsigned), () => unsigned.Value);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "small-aggregate-getter" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string operation, int bits,
            Func<long> action, Func<long> snapshot)
        {
            var before = snapshot();
            long? result = null;
            var failure = "none";
            try { result = action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "operation", operation }, { "bits", bits }, { "before", before },
                { "result", result }, { "after", snapshot() }, { "failure", failure }
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
