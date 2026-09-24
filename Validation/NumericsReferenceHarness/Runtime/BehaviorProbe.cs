using System;
using System.Collections.Generic;
using System.IO;
using NumericsReferenceFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in new[] { -17, 0, 41 })
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "input", value },
                    { "result", ReferenceKinds.Increment(value) }
                });
            }

            var field = typeof(ReferenceKinds).GetField("Number");
            if (field == null)
                throw new InvalidOperationException("The framework reference field is unavailable.");
            var zero = Activator.CreateInstance(field.FieldType);
            field.SetValue(null, zero);
            observations.Add(new Dictionary<string, object>
            {
                { "fieldType", field.FieldType.FullName },
                { "fieldAssembly", field.FieldType.Assembly.GetName().Name },
                { "zeroRoundTrip", Equals(field.GetValue(null), zero) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "numerics-reference" }, { "observations", observations }
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
