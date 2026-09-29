using System;
using System.Collections.Generic;
using System.IO;
using IntegerLiteralStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var operation = 0; operation < 6; operation++)
            foreach (var seed in new[] { int.MinValue, -1, 0, int.MaxValue })
            foreach (var value in new[] { int.MinValue, 0, int.MaxValue })
            foreach (var missing in new[] { false, true })
            foreach (var increment in new[] { int.MinValue, -7, 0, int.MaxValue })
            {
                var target = new IntegerStoreTarget { Signed = value, Unsigned = unchecked((uint)value), Neighbor = 37 };
                var owner = new IntegerStoreOwner { Marker = seed, Current = missing ? null : target };
                var exception = "none";
                try
                {
                    if (operation == 0) owner.SignedNegative();
                    else if (operation == 1) owner.SignedMinimum(increment);
                    else if (operation == 2) owner.SignedMaximum();
                    else if (operation == 3) owner.UnsignedHigh();
                    else if (operation == 4) owner.UnsignedMaximum();
                    else IntegerStoreOwner.SetParameter(owner.Current);
                }
                catch (Exception failure) { exception = failure.GetType().Name; }
                observations.Add(new Dictionary<string, object>
                {
                    { "operation", operation }, { "seed", seed }, { "value", value },
                    { "missing", missing }, { "increment", increment }, { "marker", owner.Marker },
                    { "signedAfter", target.Signed }, { "unsignedAfter", target.Unsigned },
                    { "neighborAfter", target.Neighbor }, { "exception", exception },
                    { "currentMatches", ReferenceEquals(owner.Current, missing ? null : target) }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "integer-literal-store" },
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
