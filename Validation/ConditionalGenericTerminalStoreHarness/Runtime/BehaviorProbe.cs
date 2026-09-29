using System;
using System.Collections.Generic;
using System.IO;
using ConditionalGenericTerminalStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Counters =
            { int.MinValue, -1, 0, int.MaxValue };
        private static readonly string[] Methods =
            { "flag-block", "flag-early", "pair-block", "pair-early" };

        public static void Write(string path, string stage)
        {
            var rows = new List<object>
            {
                Constructor("base", new StoreResult()),
                Constructor("string", new GenericStoreState<string>()),
                Constructor("object", new GenericStoreState<object>())
            };
            foreach (var method in Methods)
            foreach (var enabled in new[] { false, true })
            foreach (var replacement in new[] { false, true })
            foreach (var tagNull in new[] { false, true })
            foreach (var counter in Counters)
                rows.Add(Invoke(method, enabled, replacement, tagNull,
                    counter));
            foreach (var method in Methods)
            foreach (var replacement in new[] { false, true })
            foreach (var counter in IsPair(method) ? Counters : new[] { 0 })
                rows.Add(Null(method, replacement, counter));
            for (var index = 0; index < Counters.Length; index++)
                rows.Add(Repeat(index));

            Directory.CreateDirectory(Path.GetDirectoryName(
                Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(
                new Dictionary<string, object>
                {
                    { "unityVersion", Application.unityVersion },
                    { "platform", Application.platform.ToString() },
                    { "stage", stage },
                    { "profile", "conditional-generic-terminal-store" },
                    { "observations", rows }
                }));
        }

        private static Dictionary<string, object> Constructor(string owner,
            StoreResult value)
        {
            var stringState = value as GenericStoreState<string>;
            var objectState = value as GenericStoreState<object>;
            var derived = stringState != null || objectState != null;
            return new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "owner", owner },
                { "identityNull", derived ? (object)(stringState != null ?
                    stringState.Identity == null : objectState.Identity == null) : null },
                { "enabled", derived ? (object)(stringState != null ?
                    stringState.Enabled : objectState.Enabled) : null },
                { "resultFlag", derived ? (object)(stringState != null ?
                    stringState.ResultFlag : objectState.ResultFlag) : null },
                { "counter", derived ? (object)(stringState != null ?
                    stringState.Counter : objectState.Counter) : null },
                { "neighbor", derived ? (object)(stringState != null ?
                    stringState.Neighbor : objectState.Neighbor) : null },
                { "tagNull", derived ? (object)(stringState != null ?
                    stringState.Tag == null : objectState.Tag == null) : null },
                { "neighborReferenceNull", derived ? (object)(stringState != null ?
                    stringState.NeighborReference == null :
                    objectState.NeighborReference == null) : null },
                { "paddingZero", derived ? (object)PaddingZero(stringState,
                    objectState) : null }
            };
        }

        private static bool PaddingZero(GenericStoreState<string> stringState,
            GenericStoreState<object> objectState)
        {
            if (stringState != null)
                return PaddingSum(stringState) == 0;
            return PaddingSum(objectState) == 0;
        }

        private static long PaddingSum<T>(GenericStoreState<T> state)
        {
            return state.Padding00 | state.Padding01 | state.Padding02 |
                state.Padding03 | state.Padding04 | state.Padding05 |
                state.Padding06 | state.Padding07 | state.Padding08 |
                state.Padding09 | state.Padding10 | state.Padding11 |
                state.Padding12 | state.Padding13;
        }

        private static GenericStoreState<string> NewState(bool enabled,
            bool tagNull, object identity, object neighborReference,
            out string tag)
        {
            tag = tagNull ? null : new string(new[] { 't', 'a', 'g' });
            return new GenericStoreState<string>
            {
                Padding00 = 101, Padding01 = 102, Padding02 = 103,
                Padding03 = 104, Padding04 = 105, Padding05 = 106,
                Padding06 = 107, Padding07 = 108, Padding08 = 109,
                Padding09 = 110, Padding10 = 111, Padding11 = 112,
                Padding12 = 113, Padding13 = 114,
                Identity = identity, Enabled = enabled, ResultFlag = true,
                Counter = -91, Neighbor = 307, Tag = tag,
                NeighborReference = neighborReference
            };
        }

        private static bool PadsIntact(GenericStoreState<string> state)
        {
            return state.Padding00 == 101 && state.Padding01 == 102 &&
                state.Padding02 == 103 && state.Padding03 == 104 &&
                state.Padding04 == 105 && state.Padding05 == 106 &&
                state.Padding06 == 107 && state.Padding07 == 108 &&
                state.Padding08 == 109 && state.Padding09 == 110 &&
                state.Padding10 == 111 && state.Padding11 == 112 &&
                state.Padding12 == 113 && state.Padding13 == 114;
        }

        private static bool IsPair(string method) =>
            method == "pair-block" || method == "pair-early";

        private static StoreResult Call(string method,
            GenericStoreState<string> state, int counter, bool replacement)
        {
            if (method == "flag-block")
                return ConditionalGenericStores.WriteFlagBlock(state, replacement);
            if (method == "flag-early")
                return ConditionalGenericStores.WriteFlagEarly(state, replacement);
            if (method == "pair-block")
                return ConditionalGenericStores.WritePairBlock(state,
                    new CounterValue { Value = counter }, replacement);
            return ConditionalGenericStores.WritePairEarly(state,
                new CounterValue { Value = counter }, replacement);
        }

        private static Dictionary<string, object> Invoke(string method,
            bool enabled, bool replacement, bool tagNull, int counter)
        {
            var identity = new object();
            var neighborReference = new object();
            var state = NewState(enabled, tagNull, identity,
                neighborReference, out var tag);
            state.Counter = counter;
            if (IsPair(method))
                state.Counter = -91;
            StoreResult returned = null;
            var failure = "none";
            try { returned = Call(method, state, counter, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "call" }, { "method", method },
                { "enabled", enabled }, { "replacement", replacement },
                { "tagNull", tagNull }, { "incomingCounter", counter },
                { "failure", failure },
                { "sameReference", ReferenceEquals(state, returned) },
                { "returnedDerivedType", returned != null &&
                    returned.GetType() == typeof(GenericStoreState<string>) },
                { "enabledAfter", state.Enabled },
                { "resultFlagAfter", state.ResultFlag },
                { "counterAfter", state.Counter },
                { "neighborAfter", state.Neighbor },
                { "tagAfter", state.Tag },
                { "tagSame", ReferenceEquals(tag, state.Tag) },
                { "identitySame", ReferenceEquals(identity, state.Identity) },
                { "neighborReferenceSame", ReferenceEquals(neighborReference,
                    state.NeighborReference) },
                { "paddingIntact", PadsIntact(state) }
            };
        }

        private static Dictionary<string, object> Null(string method,
            bool replacement, int counter)
        {
            StoreResult returned = null;
            var failure = "none";
            try { returned = Call(method, null, counter, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null" }, { "method", method },
                { "replacement", replacement },
                { "incomingCounter", counter }, { "failure", failure },
                { "returnedNull", returned == null }
            };
        }

        private static Dictionary<string, object> Repeat(int index)
        {
            var identity = new object();
            var neighborReference = new object();
            var state = NewState(false, false, identity,
                neighborReference, out var tag);
            var first = Call("pair-early", state, Counters[index], false);
            var counterDisabled = state.Counter;
            var flagDisabled = state.ResultFlag;
            state.Enabled = true;
            var second = Call("flag-block", state, 0, false);
            var flagAfterSecond = state.ResultFlag;
            var third = Call("pair-block", state, Counters[index],
                index % 2 == 0);
            return new Dictionary<string, object>
            {
                { "kind", "repeat" }, { "incomingCounter", Counters[index] },
                { "firstSame", ReferenceEquals(state, first) },
                { "counterAfterDisabled", counterDisabled },
                { "resultAfterDisabled", flagDisabled },
                { "secondSame", ReferenceEquals(state, second) },
                { "resultAfterFlag", flagAfterSecond },
                { "thirdSame", ReferenceEquals(state, third) },
                { "counterAfter", state.Counter },
                { "resultFlagAfter", state.ResultFlag },
                { "enabledAfter", state.Enabled },
                { "neighborAfter", state.Neighbor },
                { "tagSame", ReferenceEquals(tag, state.Tag) },
                { "identitySame", ReferenceEquals(identity, state.Identity) },
                { "neighborReferenceSame", ReferenceEquals(neighborReference,
                    state.NeighborReference) },
                { "paddingIntact", PadsIntact(state) }
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
