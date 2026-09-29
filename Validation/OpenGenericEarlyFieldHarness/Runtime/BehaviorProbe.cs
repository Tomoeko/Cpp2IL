using System;
using System.Collections.Generic;
using System.IO;
using OpenGenericEarlyFieldFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Values =
        {
            int.MinValue, -65536, -129, -1, 0, 1, 127, 65535, int.MaxValue
        };

        private static void Add<T>(List<object> observations, string type, T payload, string expectedPayload)
        {
            var empty = new OpenEarlyState<T>();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "type", type }, { "first", empty.First },
                { "anchorNull", empty.Anchor == null }, { "flag", empty.Flag },
                { "payloadDefault", EqualityComparer<T>.Default.Equals(empty.Later, default(T)) }
            });
            foreach (var first in Values)
            foreach (var hasAnchor in new[] { false, true })
            {
                var anchor = hasAnchor ? new object() : null;
                var state = new OpenEarlyState<T>
                {
                    First = first, Anchor = anchor, Flag = (first & 1) != 0, Later = payload
                };
                var alias = state;
                int? firstResult = null;
                object anchorResult = null;
                bool? flagResult = null;
                var failure = "none";
                try
                {
                    firstResult = state.ReadFirst();
                    anchorResult = alias.ReadAnchor();
                    flagResult = state.ReadFlag();
                }
                catch (Exception error) { failure = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "read" }, { "type", type }, { "first", first },
                    { "anchorPresent", hasAnchor }, { "payload", expectedPayload },
                    { "firstResult", firstResult }, { "anchorSame", ReferenceEquals(anchor, anchorResult) },
                    { "flag", (first & 1) != 0 }, { "flagResult", flagResult },
                    { "firstAfter", state.First }, { "anchorAfterSame", ReferenceEquals(anchor, state.Anchor) },
                    { "flagAfter", state.Flag },
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
            OpenEarlyState<string> missing = null;
            foreach (var getter in new[] { "first", "anchor", "flag" })
            {
                var failure = "none";
                try
                {
                    if (getter == "first") missing.ReadFirst();
                    else if (getter == "anchor") missing.ReadAnchor();
                    else missing.ReadFlag();
                }
                catch (Exception error) { failure = error.GetType().FullName; }
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "null" }, { "getter", getter }, { "failure", failure }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "open-generic-early-field" },
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
