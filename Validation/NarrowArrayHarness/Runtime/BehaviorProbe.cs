using System;
using System.Collections.Generic;
using System.IO;
using NarrowArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is independent of the four selected recovery methods.
    public static class BehaviorProbe
    {
        private static readonly int[] Indices = { int.MinValue, -1, 0, 1, 3, 4, int.MaxValue };
        private static readonly byte[] ByteWrites = { 0, 127, 128, 255 };
        private static readonly sbyte[] SignedByteWrites = { -128, -1, 0, 127 };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var byteArrays = new[]
            {
                new { Label = "empty", Values = new byte[0] },
                new { Label = "single", Values = new byte[] { 255 } },
                new { Label = "mixed", Values = new byte[] { 0, 127, 128, 255 } },
                new { Label = "null", Values = (byte[])null }
            };
            foreach (var item in byteArrays)
            {
                RecordByteReads(observations, item.Label, item.Values);
                RecordByteWrites(observations, item.Label, item.Values);
            }

            var signedArrays = new[]
            {
                new { Label = "empty", Values = new sbyte[0] },
                new { Label = "single", Values = new sbyte[] { -128 } },
                new { Label = "mixed", Values = new sbyte[] { -128, -1, 0, 127 } },
                new { Label = "null", Values = (sbyte[])null }
            };
            foreach (var item in signedArrays)
            {
                RecordSignedReads(observations, item.Label, item.Values);
                RecordSignedWrites(observations, item.Label, item.Values);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "narrow-array" },
                { "observations", observations }
            }));
        }

        private static void RecordByteReads(List<object> observations, string label, byte[] values)
        {
            foreach (var index in Indices)
            {
                object result = null;
                var exception = "none";
                try { result = (int)NarrowArrayAccess.ReadByte(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "byte-read" }, { "array", label }, { "index", index },
                    { "result", result }, { "exception", exception },
                    { "valuesAfter", Snapshot(values) }
                });
            }
        }

        private static void RecordSignedReads(List<object> observations, string label, sbyte[] values)
        {
            foreach (var index in Indices)
            {
                object result = null;
                var exception = "none";
                try { result = (int)NarrowArrayAccess.ReadSignedByte(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "signed-read" }, { "array", label }, { "index", index },
                    { "result", result }, { "exception", exception },
                    { "valuesAfter", Snapshot(values) }
                });
            }
        }

        private static void RecordByteWrites(List<object> observations, string label, byte[] values)
        {
            foreach (var index in Indices)
            {
                foreach (var value in ByteWrites)
                {
                    var copy = values == null ? null : (byte[])values.Clone();
                    var alias = copy;
                    var before = Snapshot(copy);
                    var exception = "none";
                    var readBackException = "none";
                    object readBack = null;
                    try { NarrowArrayAccess.WriteByte(copy, index, value); }
                    catch (Exception error) { exception = error.GetType().FullName; }
                    if (exception == "none")
                    {
                        try { readBack = (int)NarrowArrayAccess.ReadByte(alias, index); }
                        catch (Exception error) { readBackException = error.GetType().FullName; }
                    }
                    observations.Add(new Dictionary<string, object>
                    {
                        { "kind", "byte-write" }, { "array", label }, { "index", index },
                        { "value", (int)value }, { "exception", exception },
                        { "readBack", readBack }, { "readBackException", readBackException },
                        { "valuesBefore", before }, { "valuesAfter", Snapshot(alias) }
                    });
                }
            }
        }

        private static void RecordSignedWrites(List<object> observations, string label, sbyte[] values)
        {
            foreach (var index in Indices)
            {
                foreach (var value in SignedByteWrites)
                {
                    var copy = values == null ? null : (sbyte[])values.Clone();
                    var alias = copy;
                    var before = Snapshot(copy);
                    var exception = "none";
                    var readBackException = "none";
                    object readBack = null;
                    try { NarrowArrayAccess.WriteSignedByte(copy, index, value); }
                    catch (Exception error) { exception = error.GetType().FullName; }
                    if (exception == "none")
                    {
                        try { readBack = (int)NarrowArrayAccess.ReadSignedByte(alias, index); }
                        catch (Exception error) { readBackException = error.GetType().FullName; }
                    }
                    observations.Add(new Dictionary<string, object>
                    {
                        { "kind", "signed-write" }, { "array", label }, { "index", index },
                        { "value", (int)value }, { "exception", exception },
                        { "readBack", readBack }, { "readBackException", readBackException },
                        { "valuesBefore", before }, { "valuesAfter", Snapshot(alias) }
                    });
                }
            }
        }

        private static object Snapshot(byte[] values)
        {
            if (values == null)
                return null;
            var result = new int[values.Length];
            for (var index = 0; index < values.Length; index++)
                result[index] = values[index];
            return result;
        }

        private static object Snapshot(sbyte[] values)
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
