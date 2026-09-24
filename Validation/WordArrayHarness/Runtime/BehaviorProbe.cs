using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WordArrayFixture;

namespace RecoveryValidation
{
    // This driver is independent of the two selected recovery methods.
    public static class BehaviorProbe
    {
        private static readonly int[] Indices =
        {
            int.MinValue, -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, int.MaxValue
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var signedArrays = new[]
            {
                new { Label = "empty", Values = new short[0] },
                new { Label = "single", Values = new short[] { short.MinValue } },
                new { Label = "mixed", Values = new short[]
                    { short.MinValue, -1, 0, 1, short.MaxValue, 256, -256, 12345 } },
                new { Label = "null", Values = (short[])null }
            };
            foreach (var item in signedArrays)
                RecordSignedReads(observations, item.Label, item.Values);

            var unsignedArrays = new[]
            {
                new { Label = "empty", Values = new ushort[0] },
                new { Label = "single", Values = new ushort[] { ushort.MaxValue } },
                new { Label = "mixed", Values = new ushort[]
                    { 0, 1, 255, 256, 32767, 32768, 65534, ushort.MaxValue } },
                new { Label = "null", Values = (ushort[])null }
            };
            foreach (var item in unsignedArrays)
                RecordUnsignedReads(observations, item.Label, item.Values);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "word-array" },
                { "observations", observations }
            }));
        }

        private static void RecordSignedReads(List<object> observations, string label, short[] values)
        {
            foreach (var index in Indices)
            {
                var before = Snapshot(values);
                object result = null;
                var exception = "none";
                try { result = WordArrayAccess.ReadSigned(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "signed-read" }, { "array", label }, { "index", index },
                    { "result", result }, { "exception", exception },
                    { "valuesBefore", before }, { "valuesAfter", Snapshot(values) }
                });
            }
        }

        private static void RecordUnsignedReads(List<object> observations, string label, ushort[] values)
        {
            foreach (var index in Indices)
            {
                var before = Snapshot(values);
                object result = null;
                var exception = "none";
                try { result = WordArrayAccess.ReadUnsigned(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "unsigned-read" }, { "array", label }, { "index", index },
                    { "result", result }, { "exception", exception },
                    { "valuesBefore", before }, { "valuesAfter", Snapshot(values) }
                });
            }
        }

        private static object Snapshot(short[] values)
        {
            if (values == null)
                return null;
            var result = new int[values.Length];
            for (var index = 0; index < values.Length; index++)
                result[index] = values[index];
            return result;
        }

        private static object Snapshot(ushort[] values)
        {
            if (values == null)
                return null;
            var result = new int[values.Length];
            for (var index = 0; index < values.Length; index++)
                result[index] = values[index];
            return result;
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
