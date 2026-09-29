using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using CctorBooleanGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            RuntimeHelpers.RunClassConstructor(typeof(FlagState).TypeHandle);
            var fresh = new FlagState();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default" },
                { "flag", fresh.Flag },
                { "neighbor", fresh.Neighbor },
                { "counter", fresh.Counter },
                { "first", fresh.ReadFlag() },
                { "repeat", fresh.ReadFlag() }
            });

            foreach (var flag in new[] { false, true })
            foreach (var neighbor in new[] { false, true })
            {
                var state = new FlagState
                {
                    Flag = flag, Neighbor = neighbor, Counter = 37
                };
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "read" }, { "flag", flag },
                    { "neighbor", neighbor }, { "counter", 37 },
                    { "first", state.ReadFlag() },
                    { "repeat", state.ReadFlag() },
                    { "flagAfter", state.Flag },
                    { "neighborAfter", state.Neighbor },
                    { "counterAfter", state.Counter }
                });
            }

            FlagState missing = null;
            var failure = "none";
            try { _ = missing.ReadFlag(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "failure", failure }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "cctor-boolean-getter" },
                { "observations", observations }
            }));
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
