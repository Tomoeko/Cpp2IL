using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ScalarInt32SingleConversionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Samples =
        {
            0, 1, -1, 3, -3,
            16777215, 16777216, 16777217, 16777218, 16777219,
            -16777215, -16777216, -16777217, -16777218, -16777219,
            33554431, 33554432, 33554433, 33554434, 33554435,
            -33554431, -33554432, -33554433, -33554434, -33554435,
            1073741823, 1073741825, -1073741825,
            int.MaxValue, int.MaxValue - 1, int.MinValue, int.MinValue + 1
        };

        private static readonly int[,] Ratios =
        {
            { 0, 1 }, { 0, -1 }, { 0, 0 }, { 1, 0 }, { -1, 0 },
            { 1, 3 }, { -1, 3 }, { 1, -3 }, { -1, -3 },
            { int.MinValue, int.MaxValue }, { int.MaxValue, int.MinValue },
            { int.MaxValue, 1 }, { int.MinValue, -1 },
            { 16777217, 16777216 }, { 16777219, 16777217 }, { 16777217, 16777219 },
            { -16777219, 16777217 }, { 33554435, 33554433 }, { 1073741825, 1073741823 },
            { int.MaxValue, int.MaxValue }, { int.MinValue, int.MinValue },
            { int.MinValue, 1 }, { int.MaxValue, -1 }, { 3, 16777219 }
        };

        private static readonly string[] ConversionMethods = { "StoreFirst", "StoreSecond", "Convert", "ConvertStatic" };
        private static readonly string[] NullMethods = { "StoreFirst", "StoreSecond", "Convert", "Ratio", "ScaledChoice" };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var type = typeof(ConversionHolder);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var signatures = new Dictionary<string, object>();
            foreach (var method in type.GetMethods(flags))
            {
                var signature = new List<string> { method.ReturnType.FullName };
                foreach (var parameter in method.GetParameters())
                    signature.Add(parameter.ParameterType.FullName);
                signatures.Add(method.Name, signature);
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "methods", type.GetMethods(flags).Length + type.GetConstructors(flags).Length },
                { "fields", type.GetFields(flags).Length + typeof(ConversionChoice).GetFields(flags).Length },
                { "countType", type.GetField("Count").FieldType.FullName },
                { "divisorType", type.GetField("Divisor").FieldType.FullName },
                { "choiceType", type.GetField("Choice").FieldType.FullName },
                { "firstType", type.GetField("First").FieldType.FullName },
                { "secondType", type.GetField("Second").FieldType.FullName },
                { "samplesType", type.GetField("Samples").FieldType.FullName },
                { "samplesRank", type.GetField("Samples").FieldType.GetArrayRank() },
                { "samplesElement", type.GetField("Samples").FieldType.GetElementType().FullName },
                { "batchesType", type.GetField("Batches").FieldType.FullName },
                { "batchesRank", type.GetField("Batches").FieldType.GetArrayRank() },
                { "batchesElement", type.GetField("Batches").FieldType.GetElementType().FullName },
                { "batchesNestedRank", type.GetField("Batches").FieldType.GetElementType().GetArrayRank() },
                { "batchesNestedElement", type.GetField("Batches").FieldType.GetElementType().GetElementType().FullName },
                { "enumUnderlying", Enum.GetUnderlyingType(typeof(ConversionChoice)).FullName },
                { "enumValues", new[] { (int)ConversionChoice.Zero, (int)ConversionChoice.One, (int)ConversionChoice.Two } },
                { "signatures", signatures },
                { "instanceConvertIsStatic", type.GetMethod("Convert").IsStatic },
                { "staticConvertIsStatic", type.GetMethod("ConvertStatic").IsStatic }
            });
            var defaults = State(new ConversionHolder());
            defaults.Add("kind", "defaults");
            observations.Add(defaults);
            foreach (var input in Samples)
                foreach (var method in ConversionMethods)
                    Record(observations, "operation", method, NewHolder(), input);
            for (var index = 0; index < Ratios.GetLength(0); index++)
            {
                var holder = NewHolder();
                holder.Count = Ratios[index, 0];
                holder.Divisor = Ratios[index, 1];
                Record(observations, "operation", "Ratio", holder, holder.Count);
            }
            foreach (var input in Samples)
            {
                var holder = NewHolder();
                holder.Choice = (ConversionChoice)input;
                Record(observations, "operation", "ScaledChoice", holder, input);
            }
            foreach (var method in NullMethods)
            {
                var exception = "none";
                try { Invoke(method, null, 17); }
                catch (Exception error) { exception = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "null-owner" }, { "method", method }, { "exception", exception }
                });
            }
            var original = NewHolder();
            var alias = original;
            Record(observations, "reuse", "StoreFirst", alias, int.MaxValue, original);
            Record(observations, "reuse", "StoreSecond", alias, int.MinValue, original);
            Record(observations, "reuse", "Convert", alias, 16777219, original);
            Record(observations, "reuse", "Ratio", alias, 43, original);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-int32-single-conversion" }, { "observations", observations }
            }));
        }

        private static ConversionHolder NewHolder()
        {
            var samples = new[] { 5, -17 };
            return new ConversionHolder
            {
                Count = 43, Divisor = -7, Choice = (ConversionChoice)29,
                First = BitConverter.ToSingle(BitConverter.GetBytes(0x80000000u), 0),
                Second = BitConverter.ToSingle(BitConverter.GetBytes(0x00000001u), 0),
                Samples = samples,
                Batches = new[] { samples, null, samples }
            };
        }

        private static string Bits(float value)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8");
        }

        private static Dictionary<string, object> State(ConversionHolder holder)
        {
            return new Dictionary<string, object>
            {
                { "count", holder.Count }, { "divisor", holder.Divisor }, { "choice", (int)holder.Choice },
                { "firstBits", Bits(holder.First) }, { "secondBits", Bits(holder.Second) },
                { "samples", holder.Samples == null ? null : new List<int>(holder.Samples) },
                { "batches", BatchValues(holder.Batches) },
                { "firstBatchIsSamples", holder.Batches != null && holder.Batches.Length > 0 &&
                    ReferenceEquals(holder.Batches[0], holder.Samples) },
                { "repeatedBatchAlias", holder.Batches != null && holder.Batches.Length > 2 &&
                    ReferenceEquals(holder.Batches[0], holder.Batches[2]) }
            };
        }

        private static List<object> BatchValues(int[][] batches)
        {
            if (batches == null) return null;
            var values = new List<object>();
            foreach (var batch in batches)
                values.Add(batch == null ? null : new List<int>(batch));
            return values;
        }

        private static int[] FirstBatch(int[][] batches)
        {
            return batches == null || batches.Length == 0 ? null : batches[0];
        }

        private static float? Invoke(string method, ConversionHolder holder, int input)
        {
            switch (method)
            {
                case "StoreFirst": holder.StoreFirst(input); return null;
                case "StoreSecond": holder.StoreSecond(input); return null;
                case "Convert": return holder.Convert(input);
                case "ConvertStatic": return ConversionHolder.ConvertStatic(input);
                case "Ratio": return holder.Ratio();
                case "ScaledChoice": return holder.ScaledChoice();
                default: throw new ArgumentException("Unknown conversion operation");
            }
        }

        private static void Record(List<object> observations, string kind, string method, ConversionHolder holder,
            int input, ConversionHolder original = null)
        {
            float? result = null;
            var exception = "none";
            var samples = holder.Samples;
            var batches = holder.Batches;
            var firstBatch = FirstBatch(batches);
            try { result = Invoke(method, holder, input); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var row = State(holder);
            row.Add("kind", kind);
            row.Add("method", method);
            row.Add("input", input);
            row.Add("exception", exception);
            row.Add("resultBits", result.HasValue ? Bits(result.Value) : null);
            row.Add("sameSamples", ReferenceEquals(holder.Samples, samples));
            row.Add("sameBatches", ReferenceEquals(holder.Batches, batches));
            row.Add("sameFirstBatch", ReferenceEquals(FirstBatch(holder.Batches), firstBatch));
            if (kind == "reuse")
                row.Add("sameOwner", ReferenceEquals(holder, original));
            observations.Add(row);
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
