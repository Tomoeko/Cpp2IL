using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using EngineComponentFalseTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags DeclaredMembers = BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { DeclarationFacts() };

            var active = new GameObject("neutral-active");
            Record(observations, "active", active, active.transform);
            Record(observations, "repeat", active, active.transform);

            var inactive = new GameObject("neutral-inactive");
            inactive.SetActive(false);
            Record(observations, "initially-inactive", inactive,
                inactive.transform);

            var nullOwner = new GameObject("neutral-null");
            Record(observations, "null-component", nullOwner, null);

            UnityEngine.Object.DestroyImmediate(active);
            UnityEngine.Object.DestroyImmediate(inactive);
            UnityEngine.Object.DestroyImmediate(nullOwner);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "engine-component-false-tail" },
                { "observations", observations }
            }));
        }

        private static object DeclarationFacts()
        {
            var type = typeof(GuardedEngineCall);
            var method = type.GetMethod("Disable", DeclaredMembers);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "staticClass", type.IsClass && type.IsPublic &&
                    type.IsAbstract && type.IsSealed && type.BaseType == typeof(object) },
                { "oneMethod", type.GetMethods(DeclaredMembers).Length == 1 },
                { "noConstructor", type.GetConstructors(DeclaredMembers).Length == 0 },
                { "disableSignature", method != null && method.IsPublic &&
                    method.IsStatic && method.ReturnType == typeof(void) &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(Component) }
            };
        }

        private static void Record(List<object> observations, string kind,
            GameObject owner, Component component)
        {
            bool before = owner.activeSelf;
            string exception = "none";
            try { GuardedEngineCall.Disable(component); }
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
