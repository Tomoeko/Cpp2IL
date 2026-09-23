using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ComponentFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // Independent validation driver; none of these methods belong to the recovery scope.
    public static class BehaviorProbe
    {
        private const BindingFlags FieldFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var gameObject = new GameObject("Synthetic component runtime probe");
                var asset = ScriptableObject.CreateInstance<DataAsset>();
                try
                {
                    var component = gameObject.AddComponent<MarkerBehaviour>();
                    Observe(observations, component, asset, repeat, "fresh");
                    component.Count = 17;
                    RequireField(typeof(MarkerBehaviour), "caption").SetValue(component, "neutral caption");
                    component.Configuration = asset;
                    component.Payload = new Payload { Value = -41 };
                    asset.Value = 73;
                    RequireField(typeof(DataAsset), "label").SetValue(asset, "neutral label");
                    Observe(observations, component, asset, repeat, "assigned");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    UnityEngine.Object.DestroyImmediate(asset);
                }
            }
            var helpers = new List<object>();
            foreach (var value in new[] { int.MinValue, -1, 0, 1, int.MaxValue })
                helpers.Add(new Dictionary<string, object> { { "input", value }, { "output", OrdinaryHelper.Identity(value) } });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "components" }, { "observations", observations }, { "helpers", helpers }
            }));
        }

        private static void Observe(List<object> observations, MarkerBehaviour component, DataAsset asset, int repeat, string phase)
        {
            foreach (var fieldPath in new[] { "Count", "caption", "Configuration", "Payload.Value" })
                observations.Add(ReadField(component, asset, fieldPath, repeat, phase));
            foreach (var fieldPath in new[] { "Value", "label" })
                observations.Add(ReadField(asset, asset, fieldPath, repeat, phase));
        }

        private static object ReadField(UnityEngine.Object instance, DataAsset asset, string path, int repeat, string phase)
        {
            var type = instance.GetType();
            object value = instance;
            foreach (var part in path.Split('.'))
            {
                var field = RequireField(type, part);
                if (value == null)
                    throw new InvalidOperationException("A serialized field path has a null intermediate value: " + path);
                value = field.GetValue(value);
                type = field.FieldType;
            }
            if (type == typeof(DataAsset) && value != null)
            {
                value = new Dictionary<string, object>
                {
                    { "class", value.GetType().FullName }, { "assembly", value.GetType().Assembly.GetName().Name },
                    { "sameAsset", ReferenceEquals(value, asset) }
                };
            }
            return new Dictionary<string, object>
            {
                { "repeat", repeat }, { "phase", phase }, { "class", instance.GetType().FullName },
                { "assembly", instance.GetType().Assembly.GetName().Name }, { "path", path },
                { "managedType", type.FullName }, { "value", value }
            };
        }

        private static FieldInfo RequireField(Type type, string name)
        {
            var field = type.GetField(name, FieldFlags);
            if (field == null)
                throw new MissingFieldException(type.FullName, name);
            return field;
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
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
