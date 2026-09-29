using System;
using System.Collections.Generic;
using System.IO;
using OpenGenericPrefixOperationsFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Values =
        {
            int.MinValue, -17, -1, 0, 1, 17, int.MaxValue
        };

        private static readonly int[] Deltas = { -1, 0, 1 };

        private static void Add<T>(List<object> observations, string type, T payload, string expectedPayload)
        {
            var empty = new PrefixState<T>();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "type", type },
                { "first", empty.First }, { "second", empty.Second },
                { "anchorNull", empty.Anchor == null },
                { "payloadDefault", EqualityComparer<T>.Default.Equals(empty.Later, default(T)) }
            });

            foreach (var first in Values)
            foreach (var delta in Deltas)
            {
                var anchor = new object();
                var state = new PrefixState<T>
                {
                    First = first, Second = ~first, Anchor = anchor, Later = payload
                };
                var alias = state;
                int? result = null;
                var failure = "none";
                try { result = state.UpdateAndSum(delta); }
                catch (Exception error) { failure = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "update" }, { "type", type }, { "first", first },
                    { "second", ~first }, { "delta", delta }, { "result", result },
                    { "firstAfter", state.First }, { "secondAfter", state.Second },
                    { "anchorSame", ReferenceEquals(anchor, state.Anchor) },
                    { "payloadAfter", state.Later == null ? null : state.Later.ToString() },
                    { "aliasSame", ReferenceEquals(state, alias) }, { "failure", failure }
                });
            }
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Add(observations, "int", int.MinValue, int.MinValue.ToString());
            Add(observations, "long", long.MaxValue, long.MaxValue.ToString());
            Add(observations, "string", "open", "open");
            PrefixState<string> missing = null;
            var failure = "none";
            try { missing.UpdateAndSum(1); }
            catch (Exception error) { failure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "failure", failure }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "open-generic-prefix-operations" },
                { "observations", observations }
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
