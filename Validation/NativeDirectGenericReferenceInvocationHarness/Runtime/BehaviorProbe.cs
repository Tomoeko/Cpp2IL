using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeDirectGenericReferenceInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Routes = { "echo-first", "echo-second", "wrapper-first", "wrapper-second" };
        private static readonly string[] FreshCases = { "receiver-null", "value-null", "value-a", "value-b", "negative", "overflow" };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = 0;
            var fields = 0;
            var properties = 0;
            var signatures = new Dictionary<string, object>();
            var genericArity = new Dictionary<string, object>();
            var genericParameters = new Dictionary<string, object>();
            var fieldTypes = new Dictionary<string, object>();
            var fieldAccess = new Dictionary<string, object>();
            var types = new[] { typeof(First), typeof(Second), typeof(Relay) };
            foreach (var type in types)
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                properties += type.GetProperties(flags).Length;
                foreach (var method in type.GetMethods(flags))
                {
                    var name = type.Name + "." + method.Name;
                    var signature = new List<string> { TypeName(method.ReturnType) };
                    foreach (var parameter in method.GetParameters()) signature.Add(TypeName(parameter.ParameterType));
                    signatures.Add(name, signature);
                    var arguments = method.GetGenericArguments();
                    genericArity.Add(name, arguments.Length);
                    if (arguments.Length == 0) continue;
                    var parameters = new List<object>();
                    foreach (var argument in arguments)
                    {
                        var constraints = new List<string>();
                        foreach (var constraint in argument.GetGenericParameterConstraints()) constraints.Add(TypeName(constraint));
                        parameters.Add(new Dictionary<string, object>
                        {
                            { "name", argument.Name }, { "position", argument.GenericParameterPosition },
                            { "attributes", argument.GenericParameterAttributes.ToString() }, { "constraints", constraints }
                        });
                    }
                    genericParameters.Add(name, parameters);
                }
                foreach (var field in type.GetFields(flags))
                {
                    fieldTypes.Add(type.Name + "." + field.Name, field.FieldType.FullName);
                    fieldAccess.Add(type.Name + "." + field.Name, (field.Attributes & FieldAttributes.FieldAccessMask).ToString());
                }
            }
            var firstA = new First();
            var firstB = new First();
            var secondA = new Second();
            var secondB = new Second();
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields }, { "properties", properties },
                    { "types", types.Length }, { "signatures", signatures }, { "genericArity", genericArity },
                    { "genericParameters", genericParameters }, { "fieldTypes", fieldTypes }, { "fieldAccess", fieldAccess }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "calls", new Relay().Calls },
                    { "firstCreated", !ReferenceEquals(firstA, null) }, { "secondCreated", !ReferenceEquals(secondA, null) }
                }
            };
            foreach (var route in Routes)
            {
                foreach (var kind in FreshCases)
                {
                    var shared = new Relay();
                    var other = new Relay { Calls = -13 };
                    var overflow = new Relay { Calls = int.MaxValue };
                    if (kind == "negative") shared.Calls = -2;
                    if (kind == "overflow") shared.Calls = int.MaxValue;
                    object value = route.EndsWith("first", StringComparison.Ordinal)
                        ? (object)(kind == "value-b" ? firstB : firstA)
                        : (object)(kind == "value-b" ? secondB : secondA);
                    if (kind == "value-null") value = null;
                    Record(rows, route + "-" + kind, route, kind == "receiver-null" ? null : shared, value,
                        shared, other, overflow, firstA, firstB, secondA, secondB);
                }
            }
            var reused = new Relay();
            var independent = new Relay();
            var wraps = new Relay { Calls = int.MaxValue };
            Record(rows, "alternating-first", "wrapper-first", reused, firstA, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "alternating-second", "wrapper-second", reused, secondA, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "alternating-first-null", "wrapper-first", reused, null, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "alternating-second-null", "wrapper-second", reused, null, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "repeat-first-a", "wrapper-first", reused, firstA, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "repeat-second-a", "wrapper-second", reused, secondA, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "changed-first-b", "wrapper-first", reused, firstB, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "changed-second-b", "wrapper-second", reused, secondB, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "direct-after-wrappers-first", "echo-first", reused, firstA, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "direct-after-wrappers-second", "echo-second", reused, secondB, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "independent-first-shared-input", "wrapper-first", independent, firstA, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "original-receiver-preserved", "wrapper-first", reused, firstA, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "cross-wrapper-overflow-first", "wrapper-first", wraps, firstB, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Record(rows, "cross-wrapper-overflow-second", "wrapper-second", wraps, secondB, reused, independent, wraps, firstA, firstB, secondA, secondB);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-direct-generic-reference-invocation" }, { "observations", rows }
            }));
        }

        private static string TypeName(Type type) => type.IsGenericParameter ? type.Name : type.FullName;

        private static string Identity(object value, First firstA, First firstB, Second secondA, Second secondB)
        {
            if (value == null) return "null";
            if (ReferenceEquals(value, firstA)) return "first-a";
            if (ReferenceEquals(value, firstB)) return "first-b";
            if (ReferenceEquals(value, secondA)) return "second-a";
            return ReferenceEquals(value, secondB) ? "second-b" : "other";
        }

        private static object State(Relay shared, Relay other, Relay overflow) => new Dictionary<string, object>
        {
            { "shared", shared.Calls }, { "other", other.Calls }, { "overflow", overflow.Calls }
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Invoke(Relay receiver, string route, object value)
        {
            switch (route)
            {
                case "echo-first": return receiver.Echo<First>((First)value);
                case "echo-second": return receiver.Echo<Second>((Second)value);
                case "wrapper-first": return receiver.FirstCall((First)value);
                case "wrapper-second": return receiver.SecondCall((Second)value);
                default: throw new ArgumentException("Unknown validation route.", nameof(route));
            }
        }

        private static void Record(List<object> rows, string kind, string route, Relay receiver, object value,
            Relay shared, Relay other, Relay overflow, First firstA, First firstB, Second secondA, Second secondB)
        {
            var before = State(shared, other, overflow);
            var exception = "none";
            var result = "not-returned";
            try { result = Identity(Invoke(receiver, route, value), firstA, firstB, secondA, secondB); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var receiverIdentity = receiver == null ? "null" : ReferenceEquals(receiver, shared) ? "shared" :
                ReferenceEquals(receiver, other) ? "other" : ReferenceEquals(receiver, overflow) ? "overflow" : "unknown";
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "route", route }, { "receiver", receiverIdentity },
                { "argument", Identity(value, firstA, firstB, secondA, secondB) }, { "result", result }, { "exception", exception },
                { "before", before }, { "after", State(shared, other, overflow) }
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
