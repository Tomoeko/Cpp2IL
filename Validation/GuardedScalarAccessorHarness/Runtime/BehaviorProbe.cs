using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GuardedScalarAccessorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Codes =
        {
            int.MinValue, int.MinValue + 1, -65536, -32769, -32768, -129, -128, -1,
            0, 1, 2, 127, 128, 255, 256, 32767, 32768, 65535, 65536,
            int.MaxValue - 1, int.MaxValue
        };
        private static readonly FieldInfo FlagField = typeof(ScalarState).GetField("_flag", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo CodeField = typeof(ScalarState).GetField("_code", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo StateField = typeof(ScalarHolder).GetField("State", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshState = new ScalarState();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "owner", "state" },
                { "flag", (bool)FlagField.GetValue(freshState) },
                { "code", (int)(ScalarCode)CodeField.GetValue(freshState) },
                { "neighbor", freshState.Neighbor }, { "referenceNull", freshState.Reference == null },
                { "arrayNull", freshState.ArrayNeighbor == null }
            });
            observations.Add(Constructor("holder", new ScalarHolder()));
            observations.Add(Constructor("reader", new ScalarReader()));
            foreach (var flag in new[] { false, true })
            foreach (var code in Codes)
            foreach (var neighbor in new[] { -137, 0, 911 })
            foreach (var referenceNull in new[] { false, true })
            {
                var state = new ScalarState
                {
                    Neighbor = neighbor,
                    Reference = referenceNull ? null : new object(),
                    ArrayNeighbor = new[] { neighbor, code, flag ? int.MaxValue : int.MinValue, ~code }
                };
                // These reflected writes are validation setup, separate from player-only recovery.
                FlagField.SetValue(state, flag);
                CodeField.SetValue(state, (ScalarCode)code);
                var first = new ScalarReader();
                var second = new ScalarReader();
                StateField.SetValue(first, state);
                StateField.SetValue(second, state);
                var reference = state.Reference;
                var array = state.ArrayNeighbor;
                var row = new Dictionary<string, object>
                {
                    { "kind", "read" }, { "flag", flag }, { "code", code },
                    { "neighbor", neighbor }, { "referenceNull", referenceNull },
                    { "arrayBefore", (int[])array.Clone() }
                };
                Read(row, "flagFirst", () => first.ReadFlag());
                Read(row, "codeFirst", () => (int)first.ReadCode());
                Read(row, "flagSecond", () => second.ReadFlag());
                Read(row, "codeSecond", () => (int)second.ReadCode());
                row.Add("flagAfter", (bool)FlagField.GetValue(state));
                row.Add("codeAfter", (int)(ScalarCode)CodeField.GetValue(state));
                row.Add("neighborAfter", state.Neighbor);
                row.Add("referenceSame", ReferenceEquals(reference, state.Reference));
                row.Add("arraySame", ReferenceEquals(array, state.ArrayNeighbor));
                row.Add("arrayAfter", state.ArrayNeighbor);
                row.Add("stateSame", ReferenceEquals(state, StateField.GetValue(first)));
                row.Add("aliasStateSame", ReferenceEquals(state, StateField.GetValue(second)));
                observations.Add(row);
            }
            var empty = new ScalarReader();
            ScalarReader missing = null;
            observations.Add(Null("state-flag", () => empty.ReadFlag()));
            observations.Add(Null("state-code", () => (int)empty.ReadCode()));
            observations.Add(Null("receiver-flag", () => missing.ReadFlag()));
            observations.Add(Null("receiver-code", () => (int)missing.ReadCode()));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "guarded-scalar-accessor" }, { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Constructor(string owner, ScalarHolder value)
        {
            return new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "owner", owner }, { "stateNull", StateField.GetValue(value) == null }
            };
        }

        private static void Read(Dictionary<string, object> row, string key, Func<object> action)
        {
            object value = null;
            var failure = "none";
            try { value = action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            row.Add(key, value);
            row.Add(key + "Failure", failure);
        }

        private static Dictionary<string, object> Null(string route, Func<object> action)
        {
            var row = new Dictionary<string, object> { { "kind", "null" }, { "route", route } };
            Read(row, "value", action);
            return row;
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
