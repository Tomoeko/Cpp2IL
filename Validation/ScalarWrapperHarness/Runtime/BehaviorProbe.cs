using System;
using System.Collections.Generic;
using System.IO;
using ScalarWrapperFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly uint[] NarrowValues =
        {
            0u, 1u, 0x7fffffffu, 0x80000000u, uint.MaxValue
        };

        private static readonly ulong[] WideValues =
        {
            0ul, 1ul, 0x100000000ul, 0x7ffffffffffffffful,
            0x8000000000000000ul, ulong.MaxValue
        };

        private static readonly uint[,] NarrowPairs =
        {
            { 0u, 0u }, { 0u, 1u }, { 1u, 0u },
            { 0x7fffffffu, 0x80000000u },
            { 0x80000000u, 0x7fffffffu },
            { uint.MaxValue, 0u }, { 0u, uint.MaxValue },
            { uint.MaxValue, uint.MaxValue }
        };

        private static readonly ulong[,] WidePairs =
        {
            { 0ul, 0ul }, { 0ul, 1ul }, { 1ul, 0ul },
            { 0x7ffffffffffffffful, 0x8000000000000000ul },
            { 0x8000000000000000ul, 0x7ffffffffffffffful },
            { ulong.MaxValue, 0ul }, { 0ul, ulong.MaxValue },
            { ulong.MaxValue, ulong.MaxValue },
            { 0x100000000ul, uint.MaxValue }
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in NarrowValues)
            {
                var first = new UnsignedCellA { Value = value };
                var firstText = first.ToString();
                observations.Add(ValueRow("narrow-a", value, firstText,
                    first.Value));

                var second = new UnsignedCellB { Value = value };
                var secondText = second.ToString();
                observations.Add(ValueRow("narrow-b", value, secondText,
                    second.Value));
            }

            foreach (var value in WideValues)
            {
                var wide = new WideUnsignedCell { Value = value };
                var text = wide.ToString();
                var afterText = wide.Value;
                var hash = wide.GetHashCode();
                var row = ValueRow("wide", value, text, afterText);
                row.Add("hash", hash);
                row.Add("afterHash", wide.Value);
                observations.Add(row);
            }

            for (var index = 0; index < NarrowPairs.GetLength(0); index++)
            {
                var leftValue = NarrowPairs[index, 0];
                var rightValue = NarrowPairs[index, 1];
                var firstLeft = new UnsignedCellA { Value = leftValue };
                var firstRight = new UnsignedCellA { Value = rightValue };
                var firstResult = firstLeft.CompareTo(firstRight);
                observations.Add(CompareRow("narrow-a", leftValue, rightValue,
                    firstResult, firstLeft.Value, firstRight.Value));

                var secondLeft = new UnsignedCellB { Value = leftValue };
                var secondRight = new UnsignedCellB { Value = rightValue };
                var secondResult = secondLeft.CompareTo(secondRight);
                observations.Add(CompareRow("narrow-b", leftValue, rightValue,
                    secondResult, secondLeft.Value, secondRight.Value));
            }

            for (var index = 0; index < WidePairs.GetLength(0); index++)
            {
                var leftValue = WidePairs[index, 0];
                var rightValue = WidePairs[index, 1];
                var left = new WideUnsignedCell { Value = leftValue };
                var right = new WideUnsignedCell { Value = rightValue };
                var result = left.CompareTo(right);
                observations.Add(CompareRow("wide", leftValue, rightValue,
                    result, left.Value, right.Value));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-wrapper" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> ValueRow(string kind, object value,
            string text, object afterText)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", "value" },
                { "value", value }, { "text", text },
                { "afterText", afterText }
            };
        }

        private static Dictionary<string, object> CompareRow(string kind, object left,
            object right, int result, object leftAfter, object rightAfter)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", "compare" },
                { "left", left }, { "right", right },
                { "result", result }, { "leftAfter", leftAfter },
                { "rightAfter", rightAfter }
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
