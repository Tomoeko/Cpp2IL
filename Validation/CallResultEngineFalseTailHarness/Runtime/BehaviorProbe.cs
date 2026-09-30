using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CallResultEngineFalseTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags DeclaredMembers = BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static;

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { DeclarationFacts() };
            var active = new GameObject("neutral-active");
            var activeBehaviour = active.AddComponent<ActivityBehaviour>();
            Record(observations, "active", active, activeBehaviour);
            Record(observations, "repeat", active, activeBehaviour);

            var inactive = new GameObject("neutral-inactive");
            var inactiveBehaviour = inactive.AddComponent<ActivityBehaviour>();
            inactive.SetActive(false);
            Record(observations, "initially-inactive", inactive,
                inactiveBehaviour);

            UnityEngine.Object.DestroyImmediate(active);
            UnityEngine.Object.DestroyImmediate(inactive);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "call-result-engine-false-tail" },
                { "observations", observations }
            }));
        }

        private static object DeclarationFacts()
        {
            var type = typeof(ActivityBehaviour);
            MethodInfo[] methods = type.GetMethods(DeclaredMembers);
            MethodInfo method = type.GetMethod("DisableSelf", DeclaredMembers);
            ConstructorInfo[] constructors = type.GetConstructors(DeclaredMembers);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "componentClass", type.IsClass && type.IsPublic &&
                    type.IsSealed && type.BaseType == typeof(MonoBehaviour) },
                { "oneMethod", methods.Length == 1 },
                { "oneConstructor", constructors.Length == 1 &&
                    constructors[0].IsPublic &&
                    constructors[0].GetParameters().Length == 0 },
                { "disableSignature", method != null && method.IsPublic &&
                    !method.IsStatic && method.ReturnType == typeof(void) &&
                    method.GetParameters().Length == 0 }
            };
        }

        private static void Record(List<object> observations, string kind,
            GameObject owner, ActivityBehaviour behaviour)
        {
            object before = owner.activeSelf;
            string exception = "none";
            try { behaviour.DisableSelf(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "before", before },
                { "after", owner.activeSelf },
                { "exception", exception }
            });
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
