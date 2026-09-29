using System;
using System.Collections.Generic;
using System.IO;
using BooleanLiteralStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var operation = 0; operation < 3; operation++)
            foreach (var seed in new[] { int.MinValue, -1, 0, int.MaxValue })
            foreach (var state in new[] { false, true })
            foreach (var neighbor in new[] { false, true })
            foreach (var missing in new[] { false, true })
            foreach (var increment in new[] { int.MinValue, -7, 0, int.MaxValue })
            {
                var target = new BooleanStoreTarget { State = state, Neighbor = neighbor };
                var owner = new BooleanStoreOwner { Marker = seed, Current = missing ? null : target };
                var exception = "none";
                try
                {
                    if (operation == 0)
                        owner.Enable();
                    else if (operation == 1)
                        owner.Disable();
                    else
                        owner.EnableAfterMutation(increment);
                }
                catch (Exception failure) { exception = failure.GetType().Name; }
                observations.Add(new Dictionary<string, object>
                {
                    { "operation", operation }, { "seed", seed }, { "state", state },
                    { "neighbor", neighbor }, { "missing", missing }, { "increment", increment },
                    { "marker", owner.Marker }, { "stateAfter", target.State },
                    { "neighborAfter", target.Neighbor }, { "exception", exception },
                    { "currentMatches", ReferenceEquals(owner.Current, missing ? null : target) }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "boolean-literal-store" },
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
                catch (Exception failure) { Debug.LogException(failure); Application.Quit(1); }
                return;
            }
        }
    }
}
