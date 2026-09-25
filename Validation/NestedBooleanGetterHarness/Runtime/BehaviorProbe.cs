using System;
using System.Collections.Generic;
using System.IO;
using NestedBooleanGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var before = new object();
            var firstAfter = new object();
            var secondAfter = new object();
            var first = new FlagCell { Before = -17, Flag = false, After = firstAfter, Count = -5 };
            var second = new FlagCell { Before = 29, Flag = false, After = secondAfter, Count = 101 };
            var holder = new FlagHolder { Before = before, Child = first, After = 37 };
            var observations = new List<object>
            {
                Row("first-false", "result", !holder.NestedFlag),
                Row("first-false", "count", holder.ReadCount() == -5),
                Row("first-false", "identity", ReferenceEquals(holder.Child, first)),
                Row("first-false", "neighbors", NeighborsUnchanged(holder, before, first, firstAfter, -17))
            };

            first.Flag = true;
            first.Count = int.MinValue;
            observations.Add(Row("first-true", "result", holder.NestedFlag));
            observations.Add(Row("first-true", "count", holder.ReadCount() == int.MinValue));
            observations.Add(Row("first-true", "repeat", holder.NestedFlag));
            observations.Add(Row("first-true", "neighbors", NeighborsUnchanged(holder, before, first, firstAfter, -17)));

            holder.Child = second;
            observations.Add(Row("second-false", "result", !holder.NestedFlag));
            observations.Add(Row("second-false", "count", holder.ReadCount() == 101));
            observations.Add(Row("second-false", "identity", ReferenceEquals(holder.Child, second)));
            observations.Add(Row("second-false", "neighbors", NeighborsUnchanged(holder, before, second, secondAfter, 29)));
            second.Flag = true;
            second.Count = int.MaxValue;
            observations.Add(Row("second-true", "result", holder.NestedFlag));
            observations.Add(Row("second-true", "count", holder.ReadCount() == int.MaxValue));
            observations.Add(Row("second-true", "first-unchanged", first.Flag && ReferenceEquals(first.After, firstAfter)));

            holder.Child = null;
            observations.Add(ExceptionRow("null-child", () => { var value = holder.NestedFlag; }));
            observations.Add(ExceptionRow("null-child-count", () => { var value = holder.ReadCount(); }));
            observations.Add(Row("null-child", "owner-unchanged", holder.Child == null &&
                ReferenceEquals(holder.Before, before) && holder.After == 37));

            FlagHolder missing = null;
            observations.Add(ExceptionRow("null-owner", () => { var value = missing.NestedFlag; }));
            observations.Add(ExceptionRow("null-owner-count", () => { var value = missing.ReadCount(); }));
            observations.Add(Row("final", "children-unchanged", first.Flag && second.Flag &&
                first.Before == -17 && second.Before == 29 &&
                first.Count == int.MinValue && second.Count == int.MaxValue &&
                ReferenceEquals(first.After, firstAfter) && ReferenceEquals(second.After, secondAfter)));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "nested-boolean-getter" },
                { "observations", observations }
            }));
        }

        private static bool NeighborsUnchanged(FlagHolder holder, object before,
            FlagCell child, object after, int childBefore) =>
            ReferenceEquals(holder.Before, before) && holder.After == 37 &&
            ReferenceEquals(holder.Child, child) && child.Before == childBefore &&
            ReferenceEquals(child.After, after);

        private static object Row(string subject, string check, bool result) =>
            new Dictionary<string, object>
            {
                { "subject", subject }, { "check", check }, { "result", result }
            };

        private static object ExceptionRow(string subject, Action action)
        {
            var exception = "none";
            try { action(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "subject", subject }, { "exception", exception }
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
