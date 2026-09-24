using System;
using System.Collections.Generic;
using System.IO;
using ExternalReferenceFixture;
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

            var package = typeof(ReferenceKinds).GetField("Package");
            var plugin = typeof(ReferenceKinds).GetField("Plugin");
            if (package == null || plugin == null)
                throw new InvalidOperationException("An external reference field is unavailable.");
            var packageValue = Activator.CreateInstance(package.FieldType);
            var pluginValue = Activator.CreateInstance(plugin.FieldType);
            package.SetValue(null, packageValue);
            plugin.SetValue(null, pluginValue);
            observations.Add(new Dictionary<string, object>
            {
                { "packageAssembly", package.FieldType.Assembly.GetName().Name },
                { "pluginAssembly", plugin.FieldType.Assembly.GetName().Name },
                { "packageIdentity", ReferenceEquals(package.GetValue(null), packageValue) },
                { "pluginIdentity", ReferenceEquals(plugin.GetValue(null), pluginValue) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "external-references" }, { "observations", observations }
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
