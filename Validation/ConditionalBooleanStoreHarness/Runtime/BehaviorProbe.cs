using System;
using System.Collections.Generic;
using System.IO;
using ConditionalBooleanStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly byte[] Values = { 0, 1, 127, 128, 255 };
        private static readonly int[] Counters = { int.MinValue, -1, 0, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { Constructor() };
            foreach (var enabled in new[] { false, true })
            foreach (var value in Values)
            foreach (var counter in Counters)
                observations.Add(WriteByte(enabled, value, counter));
            foreach (var enabled in new[] { false, true })
            foreach (var counter in Counters)
            foreach (var replacement in new[] { false, true })
                observations.Add(WritePair(enabled, counter, replacement));
            foreach (var value in Values)
                observations.Add(WriteNullByte(value));
            foreach (var counter in Counters)
            foreach (var replacement in new[] { false, true })
                observations.Add(WriteNullPair(counter, replacement));
            for (var index = 0; index < Values.Length; index++)
                observations.Add(Repeat(index));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "conditional-boolean-store" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Constructor()
        {
            var state = new StoreState();
            return new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "enabled", state.Enabled },
                { "value", (int)state.Value }, { "counter", state.Counter },
                { "resultFlag", state.ResultFlag }, { "neighbor", state.Neighbor },
                { "referenceNull", state.Reference == null }
            };
        }

        private static StoreState NewState(bool enabled, object reference)
        {
            return new StoreState
            {
                Enabled = enabled, Value = 17, Counter = -91, ResultFlag = true,
                Neighbor = 307, Reference = reference
            };
        }

        private static Dictionary<string, object> WriteByte(bool enabled, byte value, int counter)
        {
            var reference = new object();
            var state = NewState(enabled, reference);
            state.Counter = counter;
            StoreState returned = null;
            var failure = "none";
            try { returned = ConditionalStores.WriteByteWhenEnabled(state, value); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "byte" }, { "enabled", enabled }, { "incoming", (int)value },
                { "counterBefore", counter }, { "failure", failure },
                { "sameReference", ReferenceEquals(state, returned) },
                { "enabledAfter", state.Enabled }, { "valueAfter", (int)state.Value },
                { "counterAfter", state.Counter }, { "resultFlagAfter", state.ResultFlag },
                { "neighborAfter", state.Neighbor },
                { "referenceSame", ReferenceEquals(reference, state.Reference) }
            };
        }

        private static Dictionary<string, object> WritePair(bool enabled, int counter, bool replacement)
        {
            var reference = new object();
            var state = NewState(enabled, reference);
            StoreState returned = null;
            var failure = "none";
            try { returned = ConditionalStores.WritePairWhenEnabled(state, counter, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "pair" }, { "enabled", enabled }, { "incoming", counter },
                { "replacement", replacement }, { "failure", failure },
                { "sameReference", ReferenceEquals(state, returned) },
                { "enabledAfter", state.Enabled }, { "valueAfter", (int)state.Value },
                { "counterAfter", state.Counter }, { "resultFlagAfter", state.ResultFlag },
                { "neighborAfter", state.Neighbor },
                { "referenceSame", ReferenceEquals(reference, state.Reference) }
            };
        }

        private static Dictionary<string, object> WriteNullByte(byte value)
        {
            StoreState returned = null;
            var failure = "none";
            try { returned = ConditionalStores.WriteByteWhenEnabled(null, value); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null-byte" }, { "incoming", (int)value },
                { "failure", failure }, { "returnedNull", returned == null }
            };
        }

        private static Dictionary<string, object> WriteNullPair(int counter, bool replacement)
        {
            StoreState returned = null;
            var failure = "none";
            try { returned = ConditionalStores.WritePairWhenEnabled(null, counter, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null-pair" }, { "incoming", counter },
                { "replacement", replacement }, { "failure", failure },
                { "returnedNull", returned == null }
            };
        }

        private static Dictionary<string, object> Repeat(int index)
        {
            var reference = new object();
            var state = NewState(false, reference);
            var first = ConditionalStores.WriteByteWhenEnabled(state, Values[index]);
            var valueAfterDisabled = (int)state.Value;
            state.Enabled = true;
            var second = ConditionalStores.WriteByteWhenEnabled(state,
                unchecked((byte)(255 - Values[index])));
            var valueAfterEnabled = (int)state.Value;
            var third = ConditionalStores.WritePairWhenEnabled(state,
                Counters[index % Counters.Length], index % 2 == 0);
            return new Dictionary<string, object>
            {
                { "kind", "repeat" }, { "incoming", (int)Values[index] },
                { "firstSame", ReferenceEquals(state, first) },
                { "valueAfterDisabled", valueAfterDisabled },
                { "secondSame", ReferenceEquals(state, second) },
                { "valueAfterEnabled", valueAfterEnabled },
                { "thirdSame", ReferenceEquals(state, third) },
                { "counterAfter", state.Counter }, { "resultFlagAfter", state.ResultFlag },
                { "enabledAfter", state.Enabled }, { "neighborAfter", state.Neighbor },
                { "referenceSame", ReferenceEquals(reference, state.Reference) }
            };
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report")
                    continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
