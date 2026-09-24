using System;
using System.Collections.Generic;
using System.IO;
using ReferenceArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is independent of the three selected recovery methods.
    public static class BehaviorProbe
    {
        private static readonly int[] Indices =
        {
            int.MinValue, -1, 0, 1, 2, 3, int.MaxValue
        };

        private sealed class ArrayCase<T> where T : class
        {
            public string Label;
            public T[] Values;

            public ArrayCase(string label, T[] values)
            {
                Label = label;
                Values = values;
            }
        }

        public static void Write(string path, string stage)
        {
            var objectA = new object();
            var objectB = new object();
            var textA = new string(new[] { 'a', 'l', 'p', 'h', 'a' });
            var textB = new string(new[] { 'b', 'e', 't', 'a' });
            var exceptionA = new Exception("first");
            var exceptionB = new InvalidOperationException("second");
            var argumentA = new ArgumentException("third");
            var argumentB = new ArgumentException("fourth");
            var known = new object[]
            {
                objectA, objectB, textA, textB,
                exceptionA, exceptionB, argumentA, argumentB
            };
            var labels = new[]
            {
                "object-a", "object-b", "text-a", "text-b",
                "exception-a", "exception-b", "argument-a", "argument-b"
            };
            var observations = new List<object>();

            RecordReads(observations, "object-read", new[]
            {
                new ArrayCase<object>("null", null),
                new ArrayCase<object>("empty", new object[0]),
                new ArrayCase<object>("single", new[] { objectA }),
                new ArrayCase<object>("mixed", new object[] { null, objectA, objectB }),
                new ArrayCase<object>("covariant", new[] { textA, null, textB })
            }, ReferenceArrayAccess.ReadObject, known, labels);
            RecordReads(observations, "string-read", new[]
            {
                new ArrayCase<string>("null", null),
                new ArrayCase<string>("empty", new string[0]),
                new ArrayCase<string>("single", new[] { textA }),
                new ArrayCase<string>("mixed", new[] { null, textA, textB }),
                new ArrayCase<string>("alias", new[] { textA, textA, textB })
            }, ReferenceArrayAccess.ReadString, known, labels);
            RecordReads(observations, "class-read", new[]
            {
                new ArrayCase<Exception>("null", null),
                new ArrayCase<Exception>("empty", new Exception[0]),
                new ArrayCase<Exception>("single", new[] { exceptionA }),
                new ArrayCase<Exception>("mixed", new Exception[] { null, exceptionA, exceptionB }),
                new ArrayCase<Exception>("covariant", new[] { argumentA, null, argumentB })
            }, ReferenceArrayAccess.ReadClass, known, labels);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-array" },
                { "observations", observations }
            }));
        }

        private static void RecordReads<T>(List<object> observations, string kind,
            ArrayCase<T>[] cases, Func<T[], int, T> read, object[] known, string[] labels)
            where T : class
        {
            foreach (var item in cases)
                foreach (var index in Indices)
                {
                    var before = Snapshot(item.Values, known, labels);
                    T result = null;
                    var exception = "none";
                    try { result = read(item.Values, index); }
                    catch (Exception error) { exception = error.GetType().FullName; }
                    object sameReference = null;
                    if (exception == "none" && item.Values != null &&
                        index >= 0 && index < item.Values.Length)
                        sameReference = ReferenceEquals(result, item.Values[index]);
                    observations.Add(new Dictionary<string, object>
                    {
                        { "kind", kind }, { "array", item.Label }, { "index", index },
                        { "result", exception == "none" ? Identify(result, known, labels) : null },
                        { "sameReference", sameReference }, { "exception", exception },
                        { "valuesBefore", before },
                        { "valuesAfter", Snapshot(item.Values, known, labels) }
                    });
                }
        }

        private static object Snapshot<T>(T[] values, object[] known, string[] labels)
        {
            if (values == null)
                return null;
            var snapshot = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
                snapshot[index] = Identify(values[index], known, labels);
            return snapshot;
        }

        private static string Identify(object value, object[] known, string[] labels)
        {
            if (value == null)
                return "null";
            for (var index = 0; index < known.Length; index++)
                if (ReferenceEquals(value, known[index]))
                    return labels[index];
            return "unknown";
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
