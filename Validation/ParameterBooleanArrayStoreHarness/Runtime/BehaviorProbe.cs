using System;
using System.Collections.Generic;
using System.IO;
using ParameterBooleanArrayStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var operation = 0; operation < 9; operation++)
            for (var shape = 0; shape < 5; shape++)
            foreach (var index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue })
            foreach (var value in new[] { false, true })
            {
                var values = shape == 0 ? null : shape == 1 ? new bool[0] :
                    shape == 2 ? new[] { false } : shape == 3 ? new[] { true } :
                    new[] { true, false, true };
                var exception = "none";
                var writer = new BooleanArrayWriter();
                try
                {
                    switch (operation)
                    {
                        case 0: writer.Set(values, index, value); break;
                        case 1: writer.Clear(values, index); break;
                        case 2: writer.Fill(values, index); break;
                        case 3: writer.ClearIgnoringValue(values, index, value); break;
                        case 4: BooleanArrayFunctions.Set(values, index, value); break;
                        case 5: BooleanArrayFunctions.Clear(values, index); break;
                        case 6: BooleanArrayFunctions.Fill(values, index); break;
                        case 7: BooleanArrayFunctions.ClearIgnoringValue(values, index, value); break;
                        case 8: BooleanArrayFunctions.SetReordered(value, values, index); break;
                    }
                }
                catch (Exception failure) { exception = failure.GetType().Name; }
                observations.Add(new Dictionary<string, object>
                {
                    { "operation", operation }, { "shape", shape }, { "index", index },
                    { "value", value }, { "exception", exception },
                    { "values", values }, { "length", values == null ? -1 : values.Length }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "parameter-boolean-array-store" },
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
