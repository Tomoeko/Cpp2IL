using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeVirtualScalarZeroLeafFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private sealed class DispatchControl : ZeroPair
        {
            public override float First { get { return 1f; } }
            public override float Second { get { return -2f; } }
        }

        public static void Write(string path, string stage)
        {
            var rows = new List<object>();
            Declarations(rows);
            Marker(rows, "before-use");
            var first = new ZeroPair();
            var derived = new DerivedZero();
            ZeroPair baseAlias = first;
            ZeroPair derivedAlias = derived;
            IZeroPair firstInterface = first;
            IZeroPair derivedInterface = derived;
            ZeroPair control = new DispatchControl();
            IZeroPair controlInterface = control;
            Alias(rows, "base", first, baseAlias, firstInterface);
            Alias(rows, "derived", derived, derivedAlias, derivedInterface);
            Alias(rows, "control", control, control, controlInterface);
            Pair(rows, "base-direct", () => first.First, () => first.Second);
            Pair(rows, "base-alias", () => baseAlias.First, () => baseAlias.Second);
            Pair(rows, "base-interface", () => firstInterface.First, () => firstInterface.Second);
            Pair(rows, "derived-direct", () => derived.First, () => derived.Second);
            Pair(rows, "derived-base", () => derivedAlias.First, () => derivedAlias.Second);
            Pair(rows, "derived-interface", () => derivedInterface.First, () => derivedInterface.Second);
            Pair(rows, "control-base", () => control.First, () => control.Second);
            Pair(rows, "control-interface", () => controlInterface.First, () => controlInterface.Second);
            var input = new ZeroPair.StringZero();
            ZeroPair.IStringZero inputInterface = input;
            var values = new[] { null, "", "neutral value", "neutral \u03a9\0text" };
            for (var index = 0; index < values.Length; index++)
            {
                var value = values[index];
                Observe(rows, "unused-string", "concrete", index.ToString(), () => input.Read(value));
                Observe(rows, "unused-string", "interface", index.ToString(), () => inputInterface.Read(value));
            }
            ZeroPair nullBase = null;
            DerivedZero nullDerived = null;
            IZeroPair nullInterface = null;
            ZeroPair.StringZero nullInput = null;
            ZeroPair.IStringZero nullInputInterface = null;
            Pair(rows, "null-base", () => nullBase.First, () => nullBase.Second, "null");
            Pair(rows, "null-derived", () => nullDerived.First, () => nullDerived.Second, "null");
            Pair(rows, "null-interface", () => nullInterface.First, () => nullInterface.Second, "null");
            Observe(rows, "null", "concrete-string", "null", () => nullInput.Read(null));
            Observe(rows, "null", "concrete-string", "value", () => nullInput.Read(values[2]));
            Observe(rows, "null", "interface-string", "null", () => nullInputInterface.Read(null));
            Observe(rows, "null", "interface-string", "value", () => nullInputInterface.Read(values[2]));
            Marker(rows, "after-use");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-virtual-scalar-zero-leaf" }, { "observations", rows }
            }));
        }

        private static void Declarations(List<object> rows)
        {
            var types = new[] { typeof(IZeroPair), typeof(ZeroPair), typeof(DerivedZero),
                                typeof(ZeroPair.IStringZero), typeof(ZeroPair.StringZero) };
            var getters = new List<object>();
            var methods = 0;
            var fields = 0;
            var properties = 0;
            var initializers = 0;
            const BindingFlags declared = BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var type in types)
            {
                methods += type.GetMethods(declared).Length + type.GetConstructors(declared & ~BindingFlags.Static).Length;
                fields += type.GetFields(declared).Length;
                properties += type.GetProperties(declared).Length;
                if (type.TypeInitializer != null) { initializers++; methods++; }
                foreach (var name in new[] { "First", "Second" })
                {
                    var property = type.GetProperty(name, declared);
                    if (property == null) continue;
                    var getter = property.GetGetMethod();
                    getters.Add(new Dictionary<string, object>
                    {
                        { "owner", type.FullName }, { "property", name }, { "return", getter.ReturnType.FullName },
                        { "virtual", getter.IsVirtual }, { "abstract", getter.IsAbstract },
                        { "newSlot", (getter.Attributes & MethodAttributes.NewSlot) != 0 },
                        { "baseOwner", getter.GetBaseDefinition().DeclaringType.FullName },
                        { "parameters", getter.GetParameters().Length }
                    });
                }
            }
            var read = typeof(ZeroPair.StringZero).GetMethod("Read", declared);
            var marker = typeof(ZeroPair).GetField("Marker", declared);
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "declarations" }, { "methods", methods }, { "types", types.Length },
                { "fields", fields }, { "properties", properties }, { "typeInitializers", initializers },
                { "markerReadonly", marker.IsInitOnly && marker.IsStatic }, { "getters", getters },
                { "stringReturn", read.ReturnType.FullName }, { "stringParameters", read.GetParameters().Length },
                { "stringParameter", read.GetParameters()[0].ParameterType.FullName },
                { "stringVirtual", read.IsVirtual }, { "stringFinal", read.IsFinal },
                { "stringNested", typeof(ZeroPair.StringZero).IsNestedPublic }
            });
        }

        private static void Marker(List<object> rows, string phase)
        {
            rows.Add(new Dictionary<string, object> { { "kind", "marker" }, { "phase", phase }, { "value", ZeroPair.Marker } });
        }

        private static void Alias(List<object> rows, string name, object value, object concrete, object interfaceValue)
        {
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "alias" }, { "case", name }, { "concreteSame", ReferenceEquals(value, concrete) },
                { "interfaceSame", ReferenceEquals(value, interfaceValue) }
            });
        }

        private static void Pair(List<object> rows, string route, Func<float> first, Func<float> second, string kind = "dispatch")
        {
            Observe(rows, kind, route, "First", first);
            Observe(rows, kind, route, "Second", second);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Observe(List<object> rows, string kind, string route, string name, Func<float> call)
        {
            var bits = "none";
            var exception = "none";
            try { bits = unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(call()), 0)).ToString("x8"); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "route", route }, { "case", name },
                { "resultBits", bits }, { "exception", exception }, { "marker", ZeroPair.Marker }
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
