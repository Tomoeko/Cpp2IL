using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using RecoveryValidation;
using UnityEditor;
using UnityEngine;

namespace ComponentProbe
{
    public static class ScriptProbe
    {
        public static void Configure()
        {
            PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Unity_4_8);
            AssetDatabase.SaveAssets();
        }

        public static void Run()
        {
            if (Application.unityVersion != "2021.3.35f1")
                throw new InvalidOperationException("Component probe requires Unity 2021.3.35f1.");
            var assembly = Assembly.Load("ComponentFixture");
            var paths = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets/Fixture" })
                .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var scripts = paths.Select(path => new Dictionary<string, object>
            {
                { "path", path }, { "class", AssetDatabase.LoadAssetAtPath<MonoScript>(path).GetClass()?.FullName }
            }).ToArray();
            var gameObject = new GameObject("Synthetic component probe");
            var asset = ScriptableObject.CreateInstance(assembly.GetType("ComponentFixture.DataAsset", true));
            try
            {
                var component = gameObject.AddComponent(assembly.GetType("ComponentFixture.MarkerBehaviour", true));
                var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                    .Any(a => a.Key == "ComponentFixture" && a.Value == "serialized-identity");
                var report = new Dictionary<string, object>
                {
                    { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                    { "apiCompatibility", PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.Standalone).ToString() },
                    { "scope", "Editor script discovery and fresh-instance serialized fields; no original asset or GUID reconstruction." },
                    { "assembly", assembly.GetName().Name }, { "assemblyAttributePreserved", metadata },
                    { "ordinaryTypePreserved", assembly.GetType("ComponentFixture.OrdinaryHelper") != null },
                    { "scripts", scripts },
                    { "instances", new[] {
                        Observe(component, new[] { "Count", "caption", "Configuration", "Payload.Value" }),
                        Observe(asset, new[] { "Value", "label" })
                    } }
                };
                var directory = Path.Combine(Directory.GetCurrentDirectory(), "Reports");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "components.json"), ReportJson.Encode(report));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static object Observe(UnityEngine.Object instance, string[] paths)
        {
            var serialized = new SerializedObject(instance);
            var fields = new List<object>();
            foreach (var path in paths)
            {
                var property = serialized.FindProperty(path);
                var fieldType = instance.GetType();
                foreach (var part in path.Split('.'))
                    fieldType = fieldType?.GetField(part, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.FieldType;
                fields.Add(new Dictionary<string, object>
                {
                    { "path", path }, { "present", property != null },
                    { "managedType", fieldType?.FullName },
                    { "kind", property == null ? null : property.propertyType.ToString() }
                });
            }
            var script = serialized.FindProperty("m_Script")?.objectReferenceValue as MonoScript;
            return new Dictionary<string, object>
            {
                { "class", instance.GetType().FullName }, { "assembly", instance.GetType().Assembly.GetName().Name },
                { "scriptPath", script == null ? null : AssetDatabase.GetAssetPath(script) },
                { "scriptClass", script == null ? null : script.GetClass()?.FullName }, { "fields", fields }
            };
        }
    }
}
