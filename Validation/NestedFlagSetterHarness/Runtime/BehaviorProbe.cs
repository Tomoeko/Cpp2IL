using System;
using System.Collections.Generic;
using System.IO;
using NestedFlagSetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var ownerNeighbor = new object();
            var childAfter = new object();
            var child = new FlagChild { Flag = false, Neighbor = -17, After = childAfter };
            var owner = new FlagOwner { Neighbor = ownerNeighbor, Child = child, Marker = 23 };
            var payload = new Payload { Marker = 71 };
            var other = new FlagChild { Flag = false, Neighbor = 91, After = new object() };
            var observations = new List<object>
            {
                Row("initial", "flag-false", !child.Flag),
                Row("initial", "other-flag-false", !other.Flag)
            };

            owner.SetNestedFlag(null);
            observations.Add(Row("null-payload", "flag-true", child.Flag));
            observations.Add(Row("null-payload", "child-identity", ReferenceEquals(owner.Child, child)));
            observations.Add(Row("null-payload", "owner-neighbor", ReferenceEquals(owner.Neighbor, ownerNeighbor)));
            observations.Add(Row("null-payload", "owner-marker", owner.Marker == 23));
            observations.Add(Row("null-payload", "child-neighbor", child.Neighbor == -17));
            observations.Add(Row("null-payload", "child-after", ReferenceEquals(child.After, childAfter)));
            observations.Add(Row("null-payload", "other-child", !other.Flag && other.Neighbor == 91));

            child.Flag = false;
            owner.SetNestedFlag(payload);
            observations.Add(Row("non-null-payload", "flag-true", child.Flag));
            observations.Add(Row("non-null-payload", "payload-unchanged", payload.Marker == 71));
            observations.Add(Row("non-null-payload", "neighbors-unchanged",
                ReferenceEquals(owner.Neighbor, ownerNeighbor) && owner.Marker == 23 &&
                child.Neighbor == -17 && ReferenceEquals(child.After, childAfter)));

            owner.SetNestedFlag(payload);
            observations.Add(Row("repeat-non-null", "flag-true", child.Flag));
            observations.Add(Row("repeat-non-null", "payload-unchanged", payload.Marker == 71));
            owner.SetNestedFlag(null);
            observations.Add(Row("repeat-null", "flag-true", child.Flag));
            observations.Add(Row("repeat-null", "child-identity", ReferenceEquals(owner.Child, child)));

            owner.Child = null;
            observations.Add(ExceptionRow("null-child", "null-payload", () => owner.SetNestedFlag(null)));
            observations.Add(ExceptionRow("null-child", "non-null-payload", () => owner.SetNestedFlag(payload)));
            observations.Add(Row("null-child", "owner-unchanged",
                owner.Child == null && ReferenceEquals(owner.Neighbor, ownerNeighbor) &&
                owner.Marker == 23 && payload.Marker == 71));

            FlagOwner missing = null;
            observations.Add(ExceptionRow("null-owner", "null-payload", () => missing.SetNestedFlag(null)));
            observations.Add(ExceptionRow("null-owner", "non-null-payload", () => missing.SetNestedFlag(payload)));
            observations.Add(Row("final", "prior-child-unchanged",
                child.Flag && child.Neighbor == -17 && ReferenceEquals(child.After, childAfter)));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "unused-reference-nested-store" },
                { "observations", observations }
            }));
        }

        private static object Row(string subject, string check, bool result) =>
            new Dictionary<string, object>
            {
                { "subject", subject }, { "check", check }, { "result", result }
            };

        private static object ExceptionRow(string subject, string check, Action action)
        {
            var exception = "none";
            try { action(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "subject", subject }, { "check", check }, { "exception", exception }
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
