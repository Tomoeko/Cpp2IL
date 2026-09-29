using System;
using System.Collections.Generic;
using System.IO;
using OrderedGenericTailFieldFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Values =
        {
            int.MinValue, int.MinValue + 1, -65536, -32769, -32768, -129, -128, -1,
            0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
            int.MaxValue - 1, int.MaxValue
        };
        private static readonly long[] TailValues =
        {
            long.MinValue, long.MinValue + 1, -4294967296, -1, 0, 1,
            int.MaxValue, 4294967296, long.MaxValue
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in Values)
            {
                var state = new State(value);
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "constructor" }, { "value", value }, { "valueAfter", state.Value },
                    { "tailItem", state.Later.Item }, { "tailGuard", state.Later.Guard },
                    { "neighbor", state.Neighbor }, { "referenceNull", state.Reference == null }
                });
            }
            foreach (var value in Values)
            foreach (var item in TailValues)
            foreach (var neighbor in new[] { -137, 0, 911 })
            foreach (var referenceNull in new[] { false, true })
            {
                var guard = unchecked((int)(item ^ (item >> 32)));
                var state = new State(value)
                {
                    Later = new Tail<long> { Item = item, Guard = guard },
                    Neighbor = neighbor, Reference = referenceNull ? null : new object()
                };
                var alias = state;
                var reference = state.Reference;
                int? first = null;
                int? second = null;
                var failure = "none";
                try { first = state.ReadValue(); second = alias.ReadValue(); }
                catch (Exception error) { failure = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "read" }, { "value", value }, { "tailItem", item }, { "tailGuard", guard },
                    { "neighbor", neighbor }, { "referenceNull", referenceNull },
                    { "first", first }, { "second", second }, { "failure", failure },
                    { "valueAfter", state.Value }, { "tailItemAfter", state.Later.Item },
                    { "tailGuardAfter", state.Later.Guard }, { "neighborAfter", state.Neighbor },
                    { "referenceSame", ReferenceEquals(reference, state.Reference) },
                    { "aliasSame", ReferenceEquals(state, alias) }
                });
            }
            State missing = null;
            int? absent = null;
            var nullFailure = "none";
            try { absent = missing.ReadValue(); }
            catch (Exception error) { nullFailure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "value", absent }, { "failure", nullFailure }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "ordered-generic-tail-field" }, { "observations", observations }
            }));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
