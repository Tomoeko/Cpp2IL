using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using SideEffectClassCctorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var type = typeof(StaticCells);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "before" },
                { "events", InitializationWitness.Events },
                { "type", type.FullName }
            });

            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "first" },
                { "events", InitializationWitness.Events },
                { "marker", StaticCells.Marker },
                { "bias", Bits(StaticCells.Bias) }
            });

            StaticCells.Marker = 73;
            RuntimeHelpers.RunClassConstructor(type.TypeHandle);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "repeat" },
                { "events", InitializationWitness.Events },
                { "marker", StaticCells.Marker },
                { "bias", Bits(StaticCells.Bias) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "side-effect-class-cctor" },
                { "observations", observations }
            }));
        }

        private static string Bits(float value) =>
            BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8");

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
