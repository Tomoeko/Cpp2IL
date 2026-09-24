using System;
using System.Collections.Generic;
using System.IO;
using IteratorFactoryFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var firstMarker = new object();
            var secondMarker = new object();
            var owner = new IteratorOwner { Marker = firstMarker };
            var first = owner.Iterate();
            var second = owner.Iterate();
            var observations = new List<object>
            {
                Row("factory", "fresh", !ReferenceEquals(first, second)),
                Row("factory", "initial-current-null", first.Current == null)
            };

            owner.Marker = secondMarker;
            observations.Add(Row("first", "first-move", first.MoveNext()));
            observations.Add(Row("first", "deferred-owner-field", ReferenceEquals(first.Current, secondMarker)));
            observations.Add(Row("second", "first-move", second.MoveNext()));
            observations.Add(Row("second", "deferred-owner-field", ReferenceEquals(second.Current, secondMarker)));
            observations.Add(Row("first", "completed", !first.MoveNext()));
            observations.Add(Row("second", "completed", !second.MoveNext()));
            observations.Add(Row("owner", "field-unchanged", ReferenceEquals(owner.Marker, secondMarker)));

            var disposeException = "none";
            try { ((IDisposable)first).Dispose(); }
            catch (Exception error) { disposeException = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "subject", "first" }, { "check", "dispose-exception" },
                { "exception", disposeException }
            });

            var resetException = "none";
            try { first.Reset(); }
            catch (Exception error) { resetException = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "subject", "first" }, { "check", "reset-exception" },
                { "exception", resetException }
            });

            owner.Marker = null;
            var empty = owner.Iterate();
            observations.Add(Row("null-marker", "first-move", empty.MoveNext()));
            observations.Add(Row("null-marker", "current-null", empty.Current == null));
            observations.Add(Row("null-marker", "completed", !empty.MoveNext()));

            var other = new IteratorOwner { Marker = firstMarker };
            var otherIterator = other.Iterate();
            observations.Add(Row("other-owner", "first-move", otherIterator.MoveNext()));
            observations.Add(Row("other-owner", "captured-own-field",
                ReferenceEquals(otherIterator.Current, firstMarker)));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "iterator-factory" },
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
