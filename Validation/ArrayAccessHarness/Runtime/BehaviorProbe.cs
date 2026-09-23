using System;
using System.Collections.Generic;
using System.IO;
using ArrayAccessFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var arrays = new[]
            {
                new { Label = "empty", Values = new int[0] },
                new { Label = "single", Values = new[] { int.MinValue } },
                new { Label = "mixed", Values = new[] { -7, 0, 19, int.MaxValue } }
            };
            foreach (var array in arrays)
            {
                Record(observations, array.Label, array.Values);
                RecordWrite(observations, array.Label, array.Values);
            }
            Record(observations, "null", null);
            RecordWrite(observations, "null", null);
            var unsignedArrays = new[]
            {
                new { Label = "empty", Values = new uint[0] },
                new { Label = "single", Values = new[] { uint.MaxValue } },
                new { Label = "mixed", Values = new[] { 0U, 1U, 0x80000000U, uint.MaxValue } }
            };
            foreach (var array in unsignedArrays)
            {
                RecordUnsigned(observations, array.Label, array.Values);
                RecordUnsignedWrite(observations, array.Label, array.Values);
            }
            RecordUnsigned(observations, "null", null);
            RecordUnsignedWrite(observations, "null", null);
            var wideArrays = new[]
            {
                new { Label = "empty", Values = new long[0] },
                new { Label = "single", Values = new[] { long.MinValue } },
                new { Label = "mixed", Values = new[] { -7L, 0L, 19L, long.MaxValue } }
            };
            foreach (var array in wideArrays)
            {
                RecordWide(observations, array.Label, array.Values);
                RecordWideWrite(observations, array.Label, array.Values);
            }
            RecordWide(observations, "null", null);
            RecordWideWrite(observations, "null", null);
            var wideUnsignedArrays = new[]
            {
                new { Label = "empty", Values = new ulong[0] },
                new { Label = "single", Values = new[] { ulong.MaxValue } },
                new { Label = "mixed", Values = new[] { 0UL, 1UL, 0x8000000000000000UL, ulong.MaxValue } }
            };
            foreach (var array in wideUnsignedArrays)
            {
                RecordWideUnsigned(observations, array.Label, array.Values);
                RecordWideUnsignedWrite(observations, array.Label, array.Values);
            }
            RecordWideUnsigned(observations, "null", null);
            RecordWideUnsignedWrite(observations, "null", null);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "array-access" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string label, int[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                object result = null;
                var exception = "none";
                try { result = ArrayReads.Read(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
        }

        private static void RecordWrite(List<object> observations, string label, int[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                var copy = values == null ? null : (int[])values.Clone();
                var value = (index & 1) == 0 ? int.MaxValue : int.MinValue;
                object result = null;
                var exception = "none";
                try
                {
                    ArrayWrites.Write(copy, index, value);
                    result = copy == null ? null : (object)copy[index];
                }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "write:" + label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
        }

        private static void RecordUnsigned(List<object> observations, string label, uint[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                object result = null;
                var exception = "none";
                try { result = ArrayReads.ReadUnsigned(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "unsigned:" + label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
        }

        private static void RecordUnsignedWrite(List<object> observations, string label, uint[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                var copy = values == null ? null : (uint[])values.Clone();
                var value = (index & 1) == 0 ? uint.MaxValue : 0x80000000U;
                object result = null;
                var exception = "none";
                try
                {
                    ArrayWrites.WriteUnsigned(copy, index, value);
                    result = copy == null ? null : (object)copy[index];
                }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "unsigned-write:" + label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
        }

        private static void RecordWide(List<object> observations, string label, long[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                object result = null;
                var exception = "none";
                try { result = ArrayReads.ReadWide(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "wide:" + label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
        }

        private static void RecordWideWrite(List<object> observations, string label, long[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                var copy = values == null ? null : (long[])values.Clone();
                var value = (index & 1) == 0 ? long.MaxValue : long.MinValue;
                object result = null;
                var exception = "none";
                try
                {
                    ArrayWrites.WriteWide(copy, index, value);
                    result = copy == null ? null : (object)copy[index];
                }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "wide-write:" + label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
        }

        private static void RecordWideUnsigned(List<object> observations, string label, ulong[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                object result = null;
                var exception = "none";
                try { result = ArrayReads.ReadWideUnsigned(values, index); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "wide-unsigned:" + label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
        }

        private static void RecordWideUnsignedWrite(List<object> observations, string label, ulong[] values)
        {
            var length = values == null ? 0 : values.Length;
            foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
            {
                var copy = values == null ? null : (ulong[])values.Clone();
                var value = (index & 1) == 0 ? ulong.MaxValue : 0x8000000000000000UL;
                object result = null;
                var exception = "none";
                try
                {
                    ArrayWrites.WriteWideUnsigned(copy, index, value);
                    result = copy == null ? null : (object)copy[index];
                }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "wide-unsigned-write:" + label }, { "index", index }, { "result", result }, { "exception", exception }
                });
            }
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
