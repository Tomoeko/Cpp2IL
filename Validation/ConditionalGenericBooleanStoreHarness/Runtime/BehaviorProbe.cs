using System;
using System.Collections.Generic;
using System.IO;
using ConditionalGenericBooleanStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Counters = { int.MinValue, -1, 0, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>
            {
                Constructor("base", new StoreResult()),
                Constructor("string", new GenericStoreState<string>()),
                Constructor("object", new GenericStoreState<object>())
            };
            foreach (var enabled in new[] { false, true })
            foreach (var resultFlag in new[] { false, true })
            foreach (var tagNull in new[] { false, true })
            foreach (var counter in Counters)
                observations.Add(WriteFlag(enabled, resultFlag, tagNull, counter));
            foreach (var enabled in new[] { false, true })
            foreach (var counter in Counters)
            foreach (var resultFlag in new[] { false, true })
            foreach (var tagNull in new[] { false, true })
                observations.Add(WritePair(enabled, counter, resultFlag, tagNull));
            foreach (var resultFlag in new[] { false, true })
                observations.Add(NullFlag(resultFlag));
            foreach (var counter in Counters)
            foreach (var resultFlag in new[] { false, true })
                observations.Add(NullPair(counter, resultFlag));
            for (var index = 0; index < Counters.Length; index++)
                observations.Add(Repeat(index));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "conditional-generic-boolean-store" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Constructor(string owner, StoreResult value)
        {
            var state = value as GenericStoreState<string>;
            if (state == null)
            {
                var objectState = value as GenericStoreState<object>;
                return new Dictionary<string, object>
                {
                    { "kind", "constructor" }, { "owner", owner },
                    { "identityNull", objectState == null ? (object)null :
                        objectState.Identity == null },
                    { "enabled", objectState == null ? (object)null : objectState.Enabled },
                    { "resultFlag", objectState == null ? (object)null : objectState.ResultFlag },
                    { "counter", objectState == null ? (object)null : objectState.Counter },
                    { "neighbor", objectState == null ? (object)null : objectState.Neighbor },
                    { "tagNull", objectState == null ? (object)null : objectState.Tag == null },
                    { "neighborReferenceNull", objectState == null ? (object)null :
                        objectState.NeighborReference == null }
                };
            }
            return new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "owner", owner },
                { "identityNull", state.Identity == null },
                { "enabled", state.Enabled }, { "resultFlag", state.ResultFlag },
                { "counter", state.Counter }, { "neighbor", state.Neighbor },
                { "tagNull", state.Tag == null },
                { "neighborReferenceNull", state.NeighborReference == null }
            };
        }

        private static GenericStoreState<string> NewState(bool enabled, bool tagNull,
            object identity, object neighborReference, out string tag)
        {
            tag = tagNull ? null : new string(new[] { 't', 'a', 'g' });
            return new GenericStoreState<string>
            {
                Identity = identity, Enabled = enabled, ResultFlag = true,
                Counter = -91, Neighbor = 307, Tag = tag,
                NeighborReference = neighborReference
            };
        }

        private static Dictionary<string, object> WriteFlag(bool enabled, bool replacement,
            bool tagNull, int counter)
        {
            var identity = new object();
            var neighborReference = new object();
            var state = NewState(enabled, tagNull, identity, neighborReference, out var tag);
            state.Counter = counter;
            StoreResult returned = null;
            var failure = "none";
            try { returned = ConditionalGenericStores.WriteFlagWhenEnabled(state, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return StateResult("flag", state, returned, failure, identity,
                neighborReference, tag, enabled, replacement, tagNull, counter);
        }

        private static Dictionary<string, object> WritePair(bool enabled, int counter,
            bool replacement, bool tagNull)
        {
            var identity = new object();
            var neighborReference = new object();
            var state = NewState(enabled, tagNull, identity, neighborReference, out var tag);
            var argument = new CounterValue { Value = counter };
            StoreResult returned = null;
            var failure = "none";
            try { returned = ConditionalGenericStores.WritePairWhenEnabled(state, argument, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            var result = StateResult("pair", state, returned, failure, identity,
                neighborReference, tag, enabled, replacement, tagNull, counter);
            result.Add("argumentAfter", argument.Value);
            return result;
        }

        private static Dictionary<string, object> StateResult(string kind, GenericStoreState<string> state,
            StoreResult returned, string failure, object identity, object neighborReference,
            string tag, bool enabled, bool replacement, bool tagNull, int counter)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "enabled", enabled }, { "replacement", replacement },
                { "tagNull", tagNull }, { "incomingCounter", counter },
                { "failure", failure }, { "sameReference", ReferenceEquals(state, returned) },
                { "returnedDerivedType", returned != null &&
                    returned.GetType() == typeof(GenericStoreState<string>) },
                { "enabledAfter", state.Enabled }, { "resultFlagAfter", state.ResultFlag },
                { "counterAfter", state.Counter }, { "neighborAfter", state.Neighbor },
                { "tagAfter", state.Tag }, { "tagSame", ReferenceEquals(tag, state.Tag) },
                { "identitySame", ReferenceEquals(identity, state.Identity) },
                { "neighborReferenceSame", ReferenceEquals(neighborReference,
                    state.NeighborReference) }
            };
        }

        private static Dictionary<string, object> NullFlag(bool replacement)
        {
            StoreResult returned = null;
            var failure = "none";
            try { returned = ConditionalGenericStores.WriteFlagWhenEnabled(null, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null-flag" }, { "replacement", replacement },
                { "failure", failure }, { "returnedNull", returned == null }
            };
        }

        private static Dictionary<string, object> NullPair(int counter, bool replacement)
        {
            StoreResult returned = null;
            var failure = "none";
            try { returned = ConditionalGenericStores.WritePairWhenEnabled(null,
                new CounterValue { Value = counter }, replacement); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null-pair" }, { "incomingCounter", counter },
                { "replacement", replacement }, { "failure", failure },
                { "returnedNull", returned == null }
            };
        }

        private static Dictionary<string, object> Repeat(int index)
        {
            var identity = new object();
            var neighborReference = new object();
            var state = NewState(false, false, identity, neighborReference, out var tag);
            var first = ConditionalGenericStores.WritePairWhenEnabled(state,
                new CounterValue { Value = Counters[index] }, false);
            var counterAfterDisabled = state.Counter;
            var resultAfterDisabled = state.ResultFlag;
            state.Enabled = true;
            var second = ConditionalGenericStores.WriteFlagWhenEnabled(state, false);
            var resultAfterFlag = state.ResultFlag;
            var third = ConditionalGenericStores.WritePairWhenEnabled(state,
                new CounterValue { Value = Counters[index] }, index % 2 == 0);
            return new Dictionary<string, object>
            {
                { "kind", "repeat" }, { "incomingCounter", Counters[index] },
                { "firstSame", ReferenceEquals(state, first) },
                { "counterAfterDisabled", counterAfterDisabled },
                { "resultAfterDisabled", resultAfterDisabled },
                { "secondSame", ReferenceEquals(state, second) },
                { "resultAfterFlag", resultAfterFlag },
                { "thirdSame", ReferenceEquals(state, third) },
                { "counterAfter", state.Counter }, { "resultFlagAfter", state.ResultFlag },
                { "enabledAfter", state.Enabled }, { "neighborAfter", state.Neighbor },
                { "tagSame", ReferenceEquals(tag, state.Tag) },
                { "identitySame", ReferenceEquals(identity, state.Identity) },
                { "neighborReferenceSame", ReferenceEquals(neighborReference,
                    state.NeighborReference) }
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
