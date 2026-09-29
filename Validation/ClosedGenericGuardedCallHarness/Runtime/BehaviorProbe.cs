using System;
using System.Collections.Generic;
using System.IO;
using ClosedGenericGuardedCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Deltas =
        {
            int.MinValue, int.MinValue + 1, -65536, -129, -1, 0,
            1, 127, 65535, int.MaxValue - 1, int.MaxValue
        };
        private static readonly int[] Counters = { int.MinValue, -1, 0, 1, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>
            {
                Constructor("string", new Box<string>()),
                Constructor("object", new Box<object>()),
                Constructor("sink", new Sink())
            };
            foreach (var delta in Deltas)
            foreach (var counter in Counters)
            foreach (var tagNull in new[] { false, true })
            foreach (var sinkNull in new[] { false, true })
                observations.Add(Call(delta, counter, tagNull, sinkNull));
            foreach (var delta in Deltas)
            foreach (var counter in Counters)
                observations.Add(Repeat(delta, counter));
            foreach (var delta in Deltas)
            foreach (var sinkNull in new[] { false, true })
                observations.Add(NullReceiver(delta, sinkNull));
            foreach (var delta in Deltas)
                observations.Add(ObjectInstantiation(delta));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "closed-generic-guarded-call" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Constructor(string owner, object value)
        {
            if (value is Box<string> text)
                return new Dictionary<string, object>
                {
                    { "kind", "constructor" }, { "owner", owner },
                    { "counter", text.Counter }, { "tagNull", text.Tag == null }
                };
            if (value is Box<object> item)
                return new Dictionary<string, object>
                {
                    { "kind", "constructor" }, { "owner", owner },
                    { "counter", item.Counter }, { "tagNull", item.Tag == null }
                };
            var sink = (Sink)value;
            return new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "owner", owner },
                { "last", sink.Last }, { "neighbor", sink.Neighbor },
                { "referenceNull", sink.Reference == null }
            };
        }

        private static Dictionary<string, object> Call(int delta, int counter, bool tagNull, bool sinkNull)
        {
            var tag = tagNull ? null : new string(new[] { 't', 'a', 'g' });
            var box = new Box<string> { Tag = tag, Counter = counter };
            var reference = new object();
            var sink = sinkNull ? null : new Sink { Last = -101, Neighbor = 307, Reference = reference };
            object value = null;
            var failure = "none";
            try { value = Calls.CallAndStore(box, delta, sink); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "call" }, { "delta", delta }, { "counterBefore", counter },
                { "tagNull", tagNull }, { "sinkNull", sinkNull },
                { "value", value }, { "failure", failure }, { "counterAfter", box.Counter },
                { "tagAfter", box.Tag }, { "tagSame", ReferenceEquals(tag, box.Tag) },
                { "sinkLastAfter", sinkNull ? (object)null : sink.Last },
                { "sinkNeighborAfter", sinkNull ? (object)null : sink.Neighbor },
                { "sinkReferenceSame", sinkNull ? (object)null : ReferenceEquals(reference, sink.Reference) }
            };
        }

        private static Dictionary<string, object> Repeat(int delta, int counter)
        {
            var tag = new string(new[] { 'r', 'e', 'p', 'e', 'a', 't' });
            var box = new Box<string> { Tag = tag, Counter = counter };
            var reference = new object();
            var sink = new Sink { Last = -101, Neighbor = 307, Reference = reference };
            object first = null;
            object second = null;
            var firstFailure = "none";
            var secondFailure = "none";
            try { first = Calls.CallAndStore(box, delta, sink); }
            catch (Exception error) { firstFailure = error.GetType().FullName; }
            var firstCounter = box.Counter;
            var firstSink = sink.Last;
            try { second = Calls.CallAndStore(box, ~delta, sink); }
            catch (Exception error) { secondFailure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "repeat" }, { "delta", delta }, { "counterBefore", counter },
                { "first", first }, { "firstFailure", firstFailure },
                { "firstCounter", firstCounter }, { "firstSink", firstSink },
                { "second", second }, { "secondFailure", secondFailure },
                { "secondCounter", box.Counter }, { "secondSink", sink.Last },
                { "tagAfter", box.Tag }, { "tagSame", ReferenceEquals(tag, box.Tag) },
                { "sinkNeighborAfter", sink.Neighbor },
                { "sinkReferenceSame", ReferenceEquals(reference, sink.Reference) }
            };
        }

        private static Dictionary<string, object> NullReceiver(int delta, bool sinkNull)
        {
            var reference = new object();
            var sink = sinkNull ? null : new Sink { Last = -101, Neighbor = 307, Reference = reference };
            object value = null;
            var failure = "none";
            try { value = Calls.CallAndStore(null, delta, sink); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null-receiver" }, { "delta", delta }, { "sinkNull", sinkNull },
                { "value", value }, { "failure", failure },
                { "sinkLastAfter", sinkNull ? (object)null : sink.Last },
                { "sinkNeighborAfter", sinkNull ? (object)null : sink.Neighbor },
                { "sinkReferenceSame", sinkNull ? (object)null : ReferenceEquals(reference, sink.Reference) }
            };
        }

        private static Dictionary<string, object> ObjectInstantiation(int delta)
        {
            var tag = new object();
            var box = new Box<object> { Tag = tag, Counter = int.MaxValue };
            object value = null;
            var failure = "none";
            try { value = box.Add(delta); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "object-instantiation" }, { "delta", delta },
                { "counterBefore", int.MaxValue }, { "value", value }, { "failure", failure },
                { "counterAfter", box.Counter }, { "tagSame", ReferenceEquals(tag, box.Tag) }
            };
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
