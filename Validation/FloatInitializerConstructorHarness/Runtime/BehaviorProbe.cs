using System;
using System.Collections.Generic;
using System.IO;
using FloatInitializerConstructorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static uint Bits(float value)
        {
            return unchecked((uint)BitConverter.SingleToInt32Bits(value));
        }

        private static Dictionary<string, object> Snapshot(string kind, FloatCells cells)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "finite", Bits(cells.Finite) },
                { "negativeZero", Bits(cells.NegativeZero) },
                { "positiveInfinity", Bits(cells.PositiveInfinity) },
                { "negativeInfinity", Bits(cells.NegativeInfinity) }
            };
        }

        public static void Write(string path, string stage)
        {
            var first = new FloatCells();
            var second = new FloatCells();
            var observations = new List<object>
            {
                Snapshot("first", first),
                Snapshot("second", second)
            };
            first.Finite = 0.0f;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "mutation" },
                { "separate", !ReferenceEquals(first, second) },
                { "firstFinite", Bits(first.Finite) },
                { "secondFinite", Bits(second.Finite) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "float-initializer-constructor" },
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
