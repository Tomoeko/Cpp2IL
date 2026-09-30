using System;
using System.Collections.Generic;
using System.IO;
using AncestorCctorInterfaceGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var flag in new[] { false, true })
            foreach (var marker in new[] { int.MinValue, 0, int.MaxValue })
            {
                var gameObject = new GameObject("neutral-flag-probe");
                try
                {
                    var component = gameObject.AddComponent<FlagComponent>();
                    var initialFlag = component.Flag;
                    var initialMarker = component.Marker;
                    component.Flag = flag;
                    component.Marker = marker;
                    var contract = (IFlagReader)component;
                    observations.Add(new Dictionary<string, object>
                    {
                        { "kind", "read" }, { "flag", flag }, { "marker", marker },
                        { "initialFlag", initialFlag }, { "initialMarker", initialMarker },
                        { "first", contract.Read() }, { "second", contract.Read() },
                        { "flagAfter", component.Flag }, { "markerAfter", component.Marker },
                        { "receiverSame", ReferenceEquals(component, contract) }
                    });
                }
                finally
                {
                    if (Application.isPlaying)
                        UnityEngine.Object.Destroy(gameObject);
                    else
                        UnityEngine.Object.DestroyImmediate(gameObject);
                }
            }

            IFlagReader missing = null;
            string failure;
            try
            {
                missing.Read();
                failure = "none";
            }
            catch (Exception error)
            {
                failure = error.GetType().FullName;
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null-interface" }, { "failure", failure }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "ancestor-cctor-interface-getter" },
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
