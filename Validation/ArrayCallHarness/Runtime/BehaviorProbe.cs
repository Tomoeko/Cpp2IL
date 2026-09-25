using System;
using System.Collections.Generic;
using System.IO;
using ArrayCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var echo = new ArrayEcho();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "created", echo != null }, { "callsAfter", echo.Calls }
            });
            Record(observations, "values", echo, new[] { int.MinValue, 0, int.MaxValue });
            Record(observations, "empty", echo, new int[0]);
            Record(observations, "null-array", echo, null);
            echo.Calls = int.MaxValue;
            Record(observations, "overflow", echo, new[] { int.MinValue, int.MaxValue });
            Record(observations, "null-receiver-values", null, new[] { -17, 19 });
            Record(observations, "null-receiver-null-array", null, null);
            var derived = new DerivedEcho();
            RecordDerived(observations, "derived-values", derived, new[] { int.MinValue, 0, int.MaxValue });
            RecordDerived(observations, "derived-null-receiver", null, new[] { -17, 19 });
            RecordTwo(observations, "two-receivers", false, false);
            RecordTwo(observations, "first-null", true, false);
            RecordTwo(observations, "second-null", false, true);
            RecordFields(observations, "field-values", new ArrayEcho(), new[] { int.MinValue, 0, int.MaxValue });
            RecordFields(observations, "field-null-values", new ArrayEcho(), null);
            RecordFields(observations, "field-null-receiver", null, new[] { -17, 19 });
            RecordNullOwner(observations);
            RecordIndexed(observations, "indexed-distinct-first", new ArrayEcho(),
                new[] { 7, 8, 9 }, new[] { int.MinValue, 0, int.MaxValue }, 0);
            RecordIndexed(observations, "indexed-distinct-last", new ArrayEcho(),
                new[] { 17, 19, 23 }, new[] { int.MinValue, 0, int.MaxValue }, 2);
            RecordIndexed(observations, "indexed-null-return", new ArrayEcho(),
                new[] { 11, 13 }, null, 0);
            RecordIndexed(observations, "indexed-null-argument", new ArrayEcho(),
                null, new[] { -17, 19 }, 1);
            RecordIndexed(observations, "indexed-empty-return", new ArrayEcho(),
                new[] { 11 }, new int[0], 0);
            RecordIndexed(observations, "indexed-empty-argument", new ArrayEcho(),
                new int[0], new[] { 42 }, 0);
            RecordIndexed(observations, "indexed-negative", new ArrayEcho(),
                new[] { 1, 2 }, new[] { 3, 5 }, -1);
            RecordIndexed(observations, "indexed-upper", new ArrayEcho(),
                new[] { 1, 2, 3 }, new[] { 4, 5 }, 2);
            RecordIndexed(observations, "indexed-minimum", new ArrayEcho(),
                new[] { -17, 19 }, new[] { 3, 5 }, int.MinValue);
            RecordIndexed(observations, "indexed-maximum", new ArrayEcho(),
                new[] { -17, 19 }, new[] { 3, 5 }, int.MaxValue);
            RecordIndexed(observations, "indexed-overflow-value",
                new ArrayEcho { Calls = int.MaxValue }, new[] { 0, 1 },
                new[] { int.MinValue, int.MaxValue }, 1);
            RecordIndexed(observations, "indexed-overflow-bounds",
                new ArrayEcho { Calls = int.MaxValue }, new[] { 1, 2, 3 },
                new[] { int.MinValue, int.MaxValue }, 2);
            RecordIndexed(observations, "indexed-null-receiver", null,
                new[] { 17, 19 }, null, 0);
            RecordIndexed(observations, "indexed-null-receiver-null-argument", null,
                null, null, 0);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "array-call" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, ArrayEcho echo, int[] values)
        {
            object result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                var returned = ArrayCalls.Forward(echo, values);
                result = returned;
                sameReference = ReferenceEquals(returned, values);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result },
                { "sameReference", sameReference }, { "exception", exception },
                { "callsAfter", echo == null ? null : (object)echo.Calls }
            });
        }

        private static void RecordDerived(List<object> observations, string kind, DerivedEcho echo, int[] values)
        {
            object result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                var returned = ArrayCalls.ForwardDerived(echo, values);
                result = returned;
                sameReference = ReferenceEquals(returned, values);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result },
                { "sameReference", sameReference }, { "exception", exception },
                { "callsAfter", echo == null ? null : (object)echo.Calls }
            });
        }

        private static void RecordTwo(List<object> observations, string kind, bool nullFirst, bool nullSecond)
        {
            var first = nullFirst ? null : new ArrayEcho();
            var second = nullSecond ? null : new ArrayEcho();
            var values = new[] { int.MinValue, 0, int.MaxValue };
            object result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                var returned = ArrayCalls.ForwardTwo(first, second, values);
                result = returned;
                sameReference = ReferenceEquals(returned, values);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result }, { "sameReference", sameReference },
                { "exception", exception }, { "firstCalls", first == null ? null : (object)first.Calls },
                { "secondCalls", second == null ? null : (object)second.Calls }
            });
        }

        private static void RecordFields(List<object> observations, string kind, ArrayEcho receiver, int[] values)
        {
            var owner = new FieldForwarder { Receiver = receiver, Values = values };
            object result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                var returned = owner.ForwardField();
                result = returned;
                sameReference = ReferenceEquals(returned, values);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result },
                { "sameReference", sameReference }, { "exception", exception },
                { "callsAfter", receiver == null ? null : (object)receiver.Calls }
            });
        }

        private static void RecordNullOwner(List<object> observations)
        {
            FieldForwarder owner = null;
            var exception = "none";
            try { owner.ForwardField(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "field-null-owner" }, { "exception", exception }
            });
        }

        private static void RecordIndexed(List<object> observations, string kind,
            ArrayEcho echo, int[] values, int[] returnedValues, int index)
        {
            if (echo != null)
                echo.ReturnedValues = returnedValues;
            object result = null;
            var exception = "none";
            object callsBefore = echo == null ? null : (object)echo.Calls;
            try { result = ArrayCalls.ReadFromEcho(echo, values, index); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "index", index }, { "result", result },
                { "exception", exception }, { "callsBefore", callsBefore },
                { "callsAfter", echo == null ? null : (object)echo.Calls },
                { "argumentAfter", values },
                { "returnedAfter", echo == null ? null : echo.ReturnedValues }
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
