using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using DeclarationFixture;
using RecoveryValidation;
using UnityEngine;

namespace DeclarationProbe
{
    public static class LayoutProbe
    {
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RecordEditor()
        {
            Record("editor");
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RecordPlayer()
        {
            Record("player");
        }

        private static void Record(string stage)
        {
            var directory = Environment.GetEnvironmentVariable("CPP2IL_DECLARATION_LAYOUT_DIRECTORY");
            if (string.IsNullOrEmpty(directory))
                return;
            Directory.CreateDirectory(directory);
            var types = new List<object> { Observe(typeof(Packet)), Observe(typeof(Sequence)) };
            File.WriteAllText(Path.Combine(directory, stage + "-layout.json"), ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "types", types },
                { "scope", "Observed reflection and marshaling APIs; raw managed metadata is compared separately." }
            }));
        }

        private static object Observe(Type type)
        {
            var result = new Dictionary<string, object> { { "type", type.FullName } };
            try
            {
                var layout = type.StructLayoutAttribute;
                result["layout"] = layout == null ? null : new Dictionary<string, object>
                {
                    { "kind", layout.Value.ToString() }, { "pack", layout.Pack },
                    { "size", layout.Size }, { "charSet", layout.CharSet.ToString() }
                };
            }
            catch (Exception error) { result["layoutUnavailable"] = error.GetType().FullName; }
            try { result["marshalSize"] = Marshal.SizeOf(type); }
            catch (Exception error) { result["marshalSizeUnavailable"] = error.GetType().FullName; }
            var fields = new List<object>();
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var record = new Dictionary<string, object> { { "name", field.Name } };
                try { record["marshalOffset"] = Marshal.OffsetOf(type, field.Name).ToInt64(); }
                catch (Exception error) { record["marshalOffsetUnavailable"] = error.GetType().FullName; }
                try
                {
                    var attributes = field.GetCustomAttributes(typeof(MarshalAsAttribute), false);
                    var values = new List<object>();
                    foreach (MarshalAsAttribute attribute in attributes)
                        values.Add(attribute.Value.ToString());
                    record["marshalAs"] = values;
                }
                catch (Exception error) { record["marshalAsUnavailable"] = error.GetType().FullName; }
                fields.Add(record);
            }
            result["fields"] = fields;
            return result;
        }
    }
}
