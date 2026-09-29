using System;
using System.Collections.Generic;
using System.IO;
using NestedLiteralStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static object State(StoreTarget target)
        {
            return new Dictionary<string, object>
            {
                { "enabled", target.Enabled }, { "signed", target.Signed },
                { "unsigned", target.Unsigned }, { "neighbor", target.Neighbor }
            };
        }

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var operation = 0; operation < 5; operation++)
            foreach (var seed in new[] { int.MinValue, 0, int.MaxValue })
            foreach (var enabled in new[] { false, true })
            for (var configuration = 0; configuration < 6; configuration++)
            foreach (var prior in new[] { false, true })
            foreach (var increment in new[] { -7, int.MaxValue })
            {
                var first = new StoreTarget { Enabled = enabled, Signed = seed, Unsigned = unchecked((uint)seed), Neighbor = 37 };
                var second = new StoreTarget { Enabled = !enabled, Signed = seed ^ 37, Unsigned = unchecked((uint)(seed ^ 37)), Neighbor = 53 };
                var replacement = new StoreTarget { Enabled = !enabled, Signed = -17, Unsigned = 29u, Neighbor = 71 };
                var owner = new StoreOwner
                {
                    First = configuration == 1 || configuration == 3 ? null : first,
                    Second = configuration == 2 || configuration == 3 ? null : configuration == 4 ? first : second,
                    Replacement = replacement, Marker = seed, Prior = !prior
                };
                var input = configuration == 5 ? null : owner;
                var exception = "none";
                try
                {
                    if (operation == 0) input.BooleanPair(prior);
                    else if (operation == 1) input.SignedPair(increment);
                    else if (operation == 2) input.UnsignedPair();
                    else if (operation == 3) input.CapturedReplacement();
                    else StoreOwner.SetParameter(input);
                }
                catch (Exception failure) { exception = failure.GetType().Name; }
                observations.Add(new Dictionary<string, object>
                {
                    { "operation", operation }, { "seed", seed }, { "enabled", enabled },
                    { "configuration", configuration }, { "prior", prior }, { "increment", increment },
                    { "marker", owner.Marker }, { "priorAfter", owner.Prior },
                    { "firstAfter", State(first) }, { "secondAfter", State(second) },
                    { "replacementAfter", State(replacement) }, { "exception", exception },
                    { "firstBinding", owner.First == null ? "null" : ReferenceEquals(owner.First, first) ? "first" : "replacement" },
                    { "secondBinding", owner.Second == null ? "null" : ReferenceEquals(owner.Second, first) ? "first" : "second" }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "nested-literal-store" }, { "observations", observations }
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
