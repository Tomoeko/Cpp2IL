using System;
using System.Collections.Generic;
using System.IO;
using SequentialNullGuardFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            RecordCompare(observations, "both-null", null, null);
            RecordCompare(observations, "left-null", null, NewBox(7, 97));
            RecordCompare(observations, "right-null", NewBox(-7, 91), null);
            RecordCompare(observations, "equal-zero", NewBox(0, 31), NewBox(0, 47));
            RecordCompare(observations, "less", NewBox(-17, 32), NewBox(17, 48));
            RecordCompare(observations, "greater", NewBox(17, 33), NewBox(-17, 49));
            RecordCompare(observations, "minimum-maximum",
                NewBox(int.MinValue, 34), NewBox(int.MaxValue, 50));
            RecordCompare(observations, "maximum-minimum",
                NewBox(int.MaxValue, 35), NewBox(int.MinValue, 51));
            RecordCompare(observations, "minimum-equal",
                NewBox(int.MinValue, 36), NewBox(int.MinValue, 52));
            RecordCompare(observations, "maximum-equal",
                NewBox(int.MaxValue, 37), NewBox(int.MaxValue, 53));
            RecordCompare(observations, "equal-distinct", NewBox(17, 38), NewBox(17, 54));
            var shared = NewBox(17, 71);
            RecordCompare(observations, "same-object", shared, shared);

            RecordReadAfterAdd(observations, "null-zero", null, 0);
            RecordReadAfterAdd(observations, "null-maximum", null, int.MaxValue);
            RecordReadAfterAdd(observations, "zero", NewBox(0, 81), 0);
            RecordReadAfterAdd(observations, "mixed", NewBox(23, 82), -17);
            RecordReadAfterAdd(observations, "minimum", NewBox(int.MinValue, 83), -7);
            RecordReadAfterAdd(observations, "maximum", NewBox(int.MaxValue, 84), 1);
            RecordReadAfterAdd(observations, "wrapped", NewBox(-17, 85), int.MaxValue);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "sequential-null-guards" },
                { "observations", observations }
            }));
        }

        private static Box NewBox(int value, int neighbor)
        {
            return new Box { Value = value, Neighbor = neighbor };
        }

        private static void RecordCompare(List<object> observations, string kind,
            Box left, Box right)
        {
            object result = null;
            string exception = "none";
            try { result = GuardedComparisons.Compare(left, right); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", "compare:" + kind },
                { "result", result },
                { "exception", exception },
                { "leftValue", left == null ? null : (object)left.Value },
                { "rightValue", right == null ? null : (object)right.Value },
                { "leftNeighbor", left == null ? null : (object)left.Neighbor },
                { "rightNeighbor", right == null ? null : (object)right.Neighbor },
                { "sameReference", ReferenceEquals(left, right) }
            });
        }

        private static void RecordReadAfterAdd(List<object> observations, string kind,
            Box box, int amount)
        {
            object result = null;
            string exception = "none";
            try { result = GuardedComparisons.ReadAfterAdd(box, amount); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", "read-after-add:" + kind },
                { "amount", amount },
                { "result", result },
                { "exception", exception },
                { "value", box == null ? null : (object)box.Value },
                { "neighbor", box == null ? null : (object)box.Neighbor }
            });
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
