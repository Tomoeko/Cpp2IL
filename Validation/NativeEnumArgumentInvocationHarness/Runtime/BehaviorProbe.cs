using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeEnumArgumentInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var selector = typeof(Selector);
            var sink = typeof(Sink);
            var callers = typeof(Callers);
            var constants = new List<object>();
            foreach (var name in new[] { "Zero", "Positive", "Negative" })
            {
                var field = selector.GetField(name);
                constants.Add(new Dictionary<string, object>
                {
                    { "name", name }, { "type", field.FieldType.FullName },
                    { "literal", field.IsLiteral }, { "value", field.GetRawConstantValue() }
                });
            }
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "types", 3 },
                    { "methods", selector.GetMethods(flags).Length + sink.GetMethods(flags).Length +
                        sink.GetConstructors(flags).Length + callers.GetMethods(flags).Length },
                    { "fields", selector.GetFields(flags).Length + sink.GetFields(flags).Length },
                    { "underlying", Enum.GetUnderlyingType(selector).FullName },
                    { "backing", selector.GetField("value__", flags).FieldType.FullName },
                    { "acceptParameter", sink.GetMethod("Accept").GetParameters()[0].ParameterType.FullName },
                    { "forwardParameter", callers.GetMethod("Forward").GetParameters()[1].ParameterType.FullName },
                    { "sequenceParameter", callers.GetMethod("Sequence").GetParameters()[2].ParameterType.FullName },
                    { "constants", constants }
                },
                new Dictionary<string, object> { { "kind", "defaults" }, { "state", State(new Sink()) } }
            };
            var names = new[] { "zero", "negative", "maximum", "minimum", "first-null", "second-null", "both-null", "alias" };
            var values = new[] { 0, -29, int.MaxValue, int.MinValue, 17, 17, 0, 17 };
            for (var operation = 0; operation < 6; operation++)
            {
                for (var index = 0; index < names.Length; index++)
                {
                    var first = index == 4 || index == 6 ? null : new Sink { Calls = 10, Last = (Selector)31 };
                    var second = index == 7 ? first :
                        index == 5 || index == 6 ? null : new Sink { Calls = 20, Last = (Selector)(-37) };
                    var exception = "none";
                    try { Invoke(operation, first, second, (Selector)values[index]); }
                    catch (Exception error) { exception = error.GetType().FullName; }
                    observations.Add(new Dictionary<string, object>
                    {
                        { "kind", "invocation" }, { "operation", operation }, { "case", names[index] },
                        { "value", values[index] }, { "exception", exception },
                        { "alias", ReferenceEquals(first, second) }, { "first", State(first) }, { "second", State(second) }
                    });
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-enum-argument-invocation" }, { "observations", observations }
            }));
        }

        private static object State(Sink target)
        {
            if (target == null) return null;
            return new Dictionary<string, object> { { "calls", target.Calls }, { "last", (int)target.Last } };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(int operation, Sink first, Sink second, Selector value)
        {
            switch (operation)
            {
                case 0: Callers.Zero(first); break;
                case 1: Callers.Positive(first); break;
                case 2: Callers.Negative(first); break;
                case 3: Callers.Unnamed(first); break;
                case 4: Callers.Forward(first, value); break;
                case 5: Callers.Sequence(first, second, value); break;
                default: throw new ArgumentOutOfRangeException("operation");
            }
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
