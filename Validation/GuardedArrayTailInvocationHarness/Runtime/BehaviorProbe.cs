using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GuardedArrayTailInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        public static void Write(string path, string stage)
        {
            var rows = new List<object> { Declarations() };
            foreach (var operation in new[] { "read", "accept", "marker", "produced", "parameter" })
            {
                foreach (var index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue })
                    Observe(rows, operation + "-" + index, operation, index, int.MinValue, "ordinary");
                Observe(rows, operation + "-null-array", operation, 0, 99, "null-array");
                Observe(rows, operation + "-empty", operation, 0, 99, "empty");
                Observe(rows, operation + "-null-element", operation, 1, 99, "null-element");
                Observe(rows, operation + "-null-owner", operation, 0, 99, "null-owner");
            }
            Observe(rows, "accept-max", "accept", 2, int.MaxValue, "ordinary");
            var freshOwner = new ArrayTailOperations();
            var freshValue = new ArrayTailValue();
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" }, { "arrayNull", freshOwner.Values == null },
                { "marker", freshOwner.Marker }, { "calls", freshValue.Calls }, { "value", freshValue.Value }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "guarded-array-tail-invocation" }, { "observations", rows }
            }));
        }

        private static object Declarations()
        {
            var owner = typeof(ArrayTailOperations);
            var value = typeof(ArrayTailValue);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "owners", Ordinary(owner, 2, 6) && Ordinary(value, 2, 2) },
                { "signatures", Signature(owner, "GetValues", false, typeof(ArrayTailValue[])) &&
                    Signature(owner, "ReadTail", false, typeof(int), typeof(int)) &&
                    Signature(owner, "AcceptTail", false, typeof(void), typeof(int), typeof(int)) &&
                    Signature(owner, "ReadAfterMarker", false, typeof(int), typeof(int)) &&
                    Signature(owner, "ReadProducedTail", false, typeof(int), typeof(int)) &&
                    Signature(owner, "ReadParameterTail", true, typeof(int), typeof(ArrayTailValue[]), typeof(int)) &&
                    Signature(value, "Read", false, typeof(int)) &&
                    Signature(value, "Accept", false, typeof(void), typeof(int)) },
                { "fields", owner.GetField("Values", Declared).FieldType == typeof(ArrayTailValue[]) &&
                    owner.GetField("Marker", Declared).FieldType == typeof(int) &&
                    value.GetField("Calls", Declared).FieldType == typeof(int) &&
                    value.GetField("Value", Declared).FieldType == typeof(int) }
            };
        }

        private static bool Ordinary(Type owner, int fields, int methods)
        {
            var constructors = owner.GetConstructors(Declared);
            if (!owner.IsPublic || !owner.IsSealed || owner.BaseType != typeof(object) ||
                owner.GetFields(Declared).Length != fields || owner.GetMethods(Declared).Length != methods ||
                constructors.Length != 1 || !constructors[0].IsPublic ||
                constructors[0].GetParameters().Length != 0) return false;
            foreach (var field in owner.GetFields(Declared))
                if (!field.IsPublic || field.IsStatic) return false;
            return true;
        }

        private static bool Signature(Type owner, string name, bool isStatic, Type result, params Type[] types)
        {
            var method = owner.GetMethod(name, Declared);
            if (method == null || !method.IsPublic || method.IsStatic != isStatic || method.IsVirtual ||
                method.IsGenericMethod || method.ReturnType != result) return false;
            var arguments = method.GetParameters();
            if (arguments.Length != types.Length) return false;
            for (var index = 0; index < arguments.Length; index++)
                if (arguments[index].ParameterType != types[index]) return false;
            return true;
        }

        private static void Observe(List<object> rows, string kind, string operation, int index, int value, string setup)
        {
            var first = new ArrayTailValue { Calls = 7, Value = 4 };
            var second = setup == "null-element" ? null : new ArrayTailValue { Calls = 11, Value = -3 };
            var third = new ArrayTailValue { Calls = 13, Value = int.MaxValue };
            var values = setup == "null-array" ? null : setup == "empty" ? new ArrayTailValue[0] :
                new[] { first, second, third };
            var owner = setup == "null-owner" ? null : new ArrayTailOperations { Values = values, Marker = 5 };
            object result = null;
            var exception = "none";
            try
            {
                if (operation == "read") result = owner.ReadTail(index);
                else if (operation == "accept") owner.AcceptTail(index, value);
                else if (operation == "marker") result = owner.ReadAfterMarker(index);
                else if (operation == "produced") result = owner.ReadProducedTail(index);
                else result = ArrayTailOperations.ReadParameterTail(values, index);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "index", index }, { "argument", value },
                { "setup", setup }, { "result", result }, { "exception", exception },
                { "marker", owner == null ? null : (object)owner.Marker },
                { "firstCalls", first.Calls }, { "firstValue", first.Value },
                { "secondCalls", second == null ? null : (object)second.Calls },
                { "secondValue", second == null ? null : (object)second.Value },
                { "thirdCalls", third.Calls }, { "thirdValue", third.Value },
                { "arraySame", owner == null ? null : (object)ReferenceEquals(owner.Values, values) }
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
