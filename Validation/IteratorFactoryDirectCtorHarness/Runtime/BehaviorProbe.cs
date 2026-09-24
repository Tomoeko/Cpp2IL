using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using IteratorFactoryDirectCtorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var owner = new FactoryOwner { Marker = 17 };
            var alias = owner;
            var first = (DirectStateEnumerator)owner.Create();
            var second = (DirectStateEnumerator)owner.Create();
            IEnumerator dispatched = first;
            var observations = new List<object>
            {
                Row("factory", "fresh", !ReferenceEquals(first, second)),
                Row("first", "state-zero", first.State == 0),
                Row("second", "state-zero", second.State == 0),
                Row("first", "owner-captured", ReferenceEquals(first.Owner, alias)),
                Row("second", "owner-captured", ReferenceEquals(second.Owner, owner)),
                Row("first", "neighbor-untouched", first.Neighbor == null),
                Row("second", "neighbor-untouched", second.Neighbor == null),
                Row("first", "interface-current", ReferenceEquals(dispatched.Current, owner)),
                Row("first", "interface-completed", !dispatched.MoveNext()),
                Row("owner", "marker-unchanged", owner.Marker == 17)
            };

            first.Reset();
            first.Dispose();
            observations.Add(Row("first", "reset-keeps-state", first.State == 0));
            observations.Add(Row("first", "dispose-keeps-owner", ReferenceEquals(first.Owner, owner)));

            var other = new FactoryOwner { Marker = -17 };
            var third = (DirectStateEnumerator)other.Create();
            observations.Add(Row("other", "owner-captured", ReferenceEquals(third.Owner, other)));
            observations.Add(Row("other", "marker-unchanged", other.Marker == -17));
            observations.Add(Row("other", "neighbor-untouched", third.Neighbor == null));

            var minimum = new DirectStateEnumerator(int.MinValue);
            var maximum = new DirectStateEnumerator(int.MaxValue);
            observations.Add(Row("constructor", "signed-minimum", minimum.State == int.MinValue));
            observations.Add(Row("constructor", "signed-maximum", maximum.State == int.MaxValue));
            observations.Add(Row("constructor", "fresh-boundary-instances", !ReferenceEquals(minimum, maximum)));
            observations.Add(Row("constructor", "owner-unassigned", minimum.Owner == null && maximum.Owner == null));
            observations.Add(Row("constructor", "neighbor-unassigned", minimum.Neighbor == null && maximum.Neighbor == null));

            var exception = "none";
            try
            {
                FactoryOwner missing = null;
                missing.Create();
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "subject", "null-owner" }, { "check", "factory-call" },
                { "exception", exception }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "iterator-factory-direct-ctor" },
                { "observations", observations }
            }));
        }

        private static object Row(string subject, string check, bool result) =>
            new Dictionary<string, object>
            {
                { "subject", subject }, { "check", check }, { "result", result }
            };

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
