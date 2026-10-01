using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeNestedReferenceGetterFoldedInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Cases =
        {
            "success", "source-null", "target-null", "both-null", "payload-null",
            "holder-null", "payload-alias", "counter-negative", "counter-overflow", "target-second"
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = 0;
            var fields = 0;
            var properties = 0;
            var signatures = new Dictionary<string, object>();
            var fieldTypes = new Dictionary<string, object>();
            var fieldAccess = new Dictionary<string, object>();
            var propertyTypes = new Dictionary<string, object>();
            var readOnly = true;
            var types = new[] { typeof(Payload), typeof(SourceOwner), typeof(Node), typeof(InvocationHolder), typeof(MirrorSource) };
            foreach (var type in types)
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                properties += type.GetProperties(flags).Length;
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
                foreach (var field in type.GetFields(flags))
                {
                    fieldTypes.Add(type.Name + "." + field.Name, field.FieldType.FullName);
                    fieldAccess.Add(type.Name + "." + field.Name, (field.Attributes & FieldAttributes.FieldAccessMask).ToString());
                }
                foreach (var property in type.GetProperties(flags))
                {
                    propertyTypes.Add(type.Name + "." + property.Name, property.PropertyType.FullName);
                    readOnly &= property.GetGetMethod(true) != null && property.GetSetMethod(true) == null;
                }
            }
            var firstPayload = new Payload();
            var secondPayload = new Payload();
            var firstSource = new SourceOwner();
            var secondSource = new SourceOwner();
            SetPayload(firstSource, firstPayload);
            SetPayload(secondSource, secondPayload);
            var mirrorSource = new MirrorSource();
            SetMirrorPayload(mirrorSource, secondPayload);
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "properties", properties }, { "types", types.Length }, { "signatures", signatures },
                    { "fieldTypes", fieldTypes }, { "fieldAccess", fieldAccess },
                    { "propertyTypes", propertyTypes }, { "readOnlyProperties", readOnly }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "targetNull", new InvocationHolder().Target == null },
                    { "sourceNull", new InvocationHolder().Source == null }, { "payloadNull", new SourceOwner().Payload == null },
                    { "mirrorPayloadNull", new MirrorSource().Payload == null },
                    { "node", NodeState(new Node(), firstPayload, secondPayload) }
                }
            };
            RecordGetter(rows, "getter-default", new SourceOwner(), firstPayload, secondPayload);
            RecordGetter(rows, "getter-first", firstSource, firstPayload, secondPayload);
            RecordGetter(rows, "getter-second", secondSource, firstPayload, secondPayload);
            RecordGetter(rows, "getter-null", null, firstPayload, secondPayload);
            RecordMirrorGetter(rows, "mirror-getter-default", new MirrorSource(), firstPayload, secondPayload);
            SetMirrorPayload(mirrorSource, firstPayload);
            RecordMirrorGetter(rows, "mirror-getter-first", mirrorSource, firstPayload, secondPayload);
            SetMirrorPayload(mirrorSource, secondPayload);
            RecordMirrorGetter(rows, "mirror-getter-second", mirrorSource, firstPayload, secondPayload);
            RecordMirrorGetter(rows, "mirror-getter-null", null, firstPayload, secondPayload);
            foreach (var kind in Cases)
            {
                SetPayload(firstSource, kind == "payload-null" ? null : firstPayload);
                var first = new Node { Calls = 7, Value = kind == "payload-alias" ? firstPayload : secondPayload };
                var second = new Node { Calls = -13, Value = firstPayload };
                if (kind == "counter-negative") first.Calls = int.MinValue;
                if (kind == "counter-overflow") first.Calls = int.MaxValue;
                var owner = kind == "holder-null" ? null : new InvocationHolder
                {
                    Target = kind == "target-null" || kind == "both-null" ? null : kind == "target-second" ? second : first,
                    Source = kind == "source-null" || kind == "both-null" ? null : firstSource
                };
                Record(rows, kind, owner, first, second, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            }
            var reusedFirst = new Node { Calls = 17, Value = secondPayload };
            var reusedSecond = new Node { Calls = -13, Value = firstPayload };
            var reused = new InvocationHolder { Target = reusedFirst };
            Record(rows, "reuse-source-null", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            reused.Source = firstSource;
            reused.Target = null;
            Record(rows, "reuse-target-null", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            reused.Target = reusedFirst;
            SetPayload(firstSource, null);
            Record(rows, "reuse-payload-null", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            SetPayload(firstSource, firstPayload);
            Record(rows, "reuse-first", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            reused.Source = secondSource;
            reused.Target = reusedSecond;
            Record(rows, "reuse-second", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            Record(rows, "repeat-second", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            var other = new InvocationHolder { Target = reusedFirst, Source = firstSource };
            Record(rows, "shared-source-first", other, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            reused.Source = firstSource;
            SetPayload(firstSource, secondPayload);
            Record(rows, "shared-source-second", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            other.Target = reusedSecond;
            Record(rows, "shared-target-other", other, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            SetPayload(firstSource, null);
            Record(rows, "shared-target-null-payload", reused, reusedFirst, reusedSecond, firstSource, secondSource, firstPayload, secondPayload, mirrorSource);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-nested-reference-getter-folded-invocation" }, { "observations", rows }
            }));
        }

        private static void SetPayload(SourceOwner source, Payload payload)
        {
            // Validation setup stays outside the recovered assembly's method scope.
            typeof(SourceOwner).GetField("_payload", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(source, payload);
        }

        private static void SetMirrorPayload(MirrorSource source, Payload payload)
        {
            typeof(MirrorSource).GetField("_payload", BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(source, payload);
        }

        private static string Identity(object value, object first, object second)
        {
            if (value == null) return "null";
            if (ReferenceEquals(value, first)) return "first";
            return ReferenceEquals(value, second) ? "second" : "other";
        }

        private static object NodeState(Node node, Payload first, Payload second)
        {
            return new Dictionary<string, object> { { "calls", node.Calls }, { "value", Identity(node.Value, first, second) } };
        }

        private static object HolderState(InvocationHolder owner, Node first, Node second, SourceOwner firstSource, SourceOwner secondSource)
        {
            if (owner == null) return null;
            return new Dictionary<string, object>
            {
                { "target", Identity(owner.Target, first, second) }, { "source", Identity(owner.Source, firstSource, secondSource) }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner) => owner.Forward();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Payload ReadGetter(SourceOwner source) => source.Payload;

        private static void RecordGetter(List<object> rows, string kind, SourceOwner source, Payload first, Payload second)
        {
            var exception = "none";
            var result = "not-returned";
            try { result = Identity(ReadGetter(source), first, second); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object> { { "kind", kind }, { "exception", exception }, { "payload", result } });
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Payload ReadMirrorGetter(MirrorSource source) => source.Payload;

        private static void RecordMirrorGetter(List<object> rows, string kind, MirrorSource source, Payload first, Payload second)
        {
            var exception = "none";
            var result = "not-returned";
            try { result = Identity(ReadMirrorGetter(source), first, second); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object> { { "kind", kind }, { "exception", exception }, { "payload", result } });
        }

        private static void Record(List<object> rows, string kind, InvocationHolder owner, Node first, Node second,
            SourceOwner firstSource, SourceOwner secondSource, Payload firstPayload, Payload secondPayload, MirrorSource mirrorSource)
        {
            var before = HolderState(owner, first, second, firstSource, secondSource);
            var exception = "none";
            try { Invoke(owner); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception }, { "before", before },
                { "holder", HolderState(owner, first, second, firstSource, secondSource) },
                { "first", NodeState(first, firstPayload, secondPayload) }, { "second", NodeState(second, firstPayload, secondPayload) },
                { "firstSource", Identity(firstSource.Payload, firstPayload, secondPayload) },
                { "secondSource", Identity(secondSource.Payload, firstPayload, secondPayload) },
                { "mirrorSource", Identity(mirrorSource.Payload, firstPayload, secondPayload) }
            });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
