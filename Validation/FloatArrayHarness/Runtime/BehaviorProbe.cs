using System;
using System.Collections.Generic;
using System.IO;
using FloatArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is independent of the two selected recovery methods.
    public static class BehaviorProbe
    {
        private static readonly int[] Indices =
        {
            int.MinValue, -1, 0, 1, 4, 9, 10, int.MaxValue
        };

        private static readonly int[] ValueBits =
        {
            0x00000000, unchecked((int)0x80000000),
            0x7F800000, unchecked((int)0xFF800000),
            0x7FC00001, 0x7FC12345,
            0x00000001, 0x007FFFFF,
            unchecked((int)0x80000001), unchecked((int)0x807FFFFF)
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var arrays = new[]
            {
                new { Label = "empty", Values = new float[0] },
                new { Label = "single", Values = Create(0x7FC00001) },
                new { Label = "mixed", Values = Create(ValueBits) },
                new { Label = "null", Values = (float[])null }
            };

            foreach (var item in arrays)
            {
                RecordReads(observations, item.Label, item.Values);
                RecordWrites(observations, item.Label, item.Values);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "float-array" },
                { "observations", observations }
            }));
        }

        private static void RecordReads(List<object> observations, string label, float[] values)
        {
            foreach (var index in Indices)
            {
                object resultBits = null;
                var exception = "none";
                try { resultBits = Bits(FloatArrayAccess.Read(values, index)); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "float-read" }, { "array", label }, { "index", index },
                    { "resultBits", resultBits }, { "exception", exception },
                    { "valuesAfterBits", SnapshotBits(values) }
                });
            }
        }

        private static void RecordWrites(List<object> observations, string label, float[] values)
        {
            foreach (var index in Indices)
            {
                foreach (var valueBits in ValueBits)
                {
                    var copy = values == null ? null : (float[])values.Clone();
                    var alias = copy;
                    var beforeBits = SnapshotBits(copy);
                    var exception = "none";
                    var readBackException = "none";
                    object readBackBits = null;
                    try { FloatArrayAccess.Write(copy, index, FromBits(valueBits)); }
                    catch (Exception error) { exception = error.GetType().FullName; }
                    if (exception == "none")
                    {
                        try { readBackBits = Bits(FloatArrayAccess.Read(alias, index)); }
                        catch (Exception error) { readBackException = error.GetType().FullName; }
                    }
                    observations.Add(new Dictionary<string, object>
                    {
                        { "kind", "float-write" }, { "array", label }, { "index", index },
                        { "valueBits", valueBits }, { "exception", exception },
                        { "readBackBits", readBackBits }, { "readBackException", readBackException },
                        { "valuesBeforeBits", beforeBits }, { "valuesAfterBits", SnapshotBits(alias) }
                    });
                }
            }
        }

        private static float[] Create(params int[] bits)
        {
            var values = new float[bits.Length];
            for (var index = 0; index < bits.Length; index++)
                values[index] = FromBits(bits[index]);
            return values;
        }

        private static int[] SnapshotBits(float[] values)
        {
            if (values == null)
                return null;
            var bits = new int[values.Length];
            for (var index = 0; index < values.Length; index++)
                bits[index] = Bits(values[index]);
            return bits;
        }

        private static int Bits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static float FromBits(int bits)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
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
                catch (Exception error)
                {
                    Debug.LogException(error);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
