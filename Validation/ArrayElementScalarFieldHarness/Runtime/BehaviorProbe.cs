using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ArrayElementScalarFieldFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Methods =
            { "level", "amount", "fraction" };
        private static readonly int[] Indices =
            { -2, -1, 0, 1, 2, 3, int.MaxValue };
        private static readonly int[] EdgeIndices = { -1, 0, 3 };
        private static readonly int[] Levels =
            { int.MinValue, -19, int.MaxValue };
        private static readonly int[] Amounts =
            { int.MaxValue, 47, int.MinValue };
        private static readonly float[] Fractions =
            { -1.25f, 0.5f, 15.75f };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var method in Methods)
            {
                for (var length = 0; length <= 3; length++)
                    foreach (var index in Indices)
                        observations.Add(Observe(method, "length-" + length,
                            length, index));
                foreach (var index in EdgeIndices)
                    observations.Add(Observe(method, "array-null", null, index));
                observations.Add(Observe(method, "element-null", 2, 0));
                observations.Add(Observe(method, "element-null", 2, 1));
                foreach (var index in EdgeIndices)
                    observations.Add(Observe(method, "owner-null", null, index));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "array-element-scalar-field" },
                { "observations", observations }
            }));
        }

        private static object Observe(string method, string kind,
            int? length, int index)
        {
            var nodes = kind == "array-null" || kind == "owner-null"
                ? null : CreateNodes(length.Value, kind);
            var reader = kind == "owner-null" ? null : new Reader
            {
                Before = -53, Spacer = 61, Nodes = nodes, After = -67
            };
            var elements = nodes == null ? null : (Node[])nodes.Clone();
            var beforeLevels = new List<object>();
            var beforeAmounts = new List<object>();
            var beforeFractions = new List<object>();
            if (nodes != null)
                foreach (var node in nodes)
                {
                    beforeLevels.Add(node == null ? null : (object)node.Level);
                    beforeAmounts.Add(node == null ? null : (object)node.Amount);
                    beforeFractions.Add(node == null ? null :
                        (object)FormatFraction(node.Fraction));
                }
            var exception = "none";
            object result = null;
            try
            {
                switch (method)
                {
                    case "level": result = reader.ReadLevel(index); break;
                    case "amount": result = reader.ReadAmount(index); break;
                    default: result = FormatFraction(reader.ReadFraction((ToneId)index)); break;
                }
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            var levels = new List<object>();
            var amounts = new List<object>();
            var fractions = new List<object>();
            var sameElements = true;
            if (nodes != null)
                for (var item = 0; item < nodes.Length; item++)
                {
                    sameElements &= ReferenceEquals(nodes[item], elements[item]);
                    levels.Add(nodes[item] == null ? null : (object)nodes[item].Level);
                    amounts.Add(nodes[item] == null ? null : (object)nodes[item].Amount);
                    fractions.Add(nodes[item] == null ? null :
                        (object)FormatFraction(nodes[item].Fraction));
                }
            return new Dictionary<string, object>
            {
                { "method", method }, { "kind", kind },
                { "length", length }, { "index", index },
                { "exception", exception }, { "result", result },
                { "levels", levels }, { "amounts", amounts },
                { "fractions", fractions },
                { "sameArray", reader == null ? null :
                    (object)ReferenceEquals(nodes, reader.Nodes) },
                { "sameElements", nodes == null ? null : (object)sameElements },
                { "unchangedLevels", ListsEqual(beforeLevels, levels) },
                { "unchangedAmounts", ListsEqual(beforeAmounts, amounts) },
                { "unchangedFractions", ListsEqual(beforeFractions, fractions) },
                { "before", reader == null ? null : (object)reader.Before },
                { "spacer", reader == null ? null : (object)reader.Spacer },
                { "after", reader == null ? null : (object)reader.After }
            };
        }

        private static bool ListsEqual(List<object> left, List<object> right)
        {
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
                if (!Equals(left[index], right[index])) return false;
            return true;
        }

        private static string FormatFraction(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static Node[] CreateNodes(int length, string kind)
        {
            var result = new Node[length];
            for (var item = 0; item < length; item++)
                result[item] = new Node
                {
                    Level = Levels[item], Amount = Amounts[item],
                    Fraction = Fractions[item],
                    Gap0 = 7, Gap1 = -11, Gap2 = 13
                };
            if (kind == "element-null") result[0] = null;
            return result;
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
