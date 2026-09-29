using System;
using System.Collections.Generic;
using System.IO;
using CallBeforeCaptureBooleanStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public sealed class ProbeOwner : StoreOwner
    {
        public StoreTarget Selected
        {
            get { return Current; }
            set { Current = value; }
        }
    }

    public static class BehaviorProbe
    {
        private static readonly int[] Counters = { int.MinValue, -1, 0, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { Constructor(), NullOwner() };
            foreach (var counter in Counters)
            foreach (var mode in new[] { 0, 1, 2, 3 })
            foreach (var flag in new[] { false, true })
                observations.Add(Invoke(counter, mode, flag));
            observations.Add(RepeatWithNewCurrent());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "call-before-capture-boolean-store" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Constructor()
        {
            var owner = new ProbeOwner();
            var target = new StoreTarget();
            return new Dictionary<string, object>
            {
                { "kind", "constructor" },
                { "currentNull", owner.Selected == null },
                { "otherNull", owner.Other == null },
                { "counter", owner.Counter },
                { "ownerSentinel", owner.Sentinel },
                { "flag", target.Flag },
                { "neighbor", target.Neighbor },
                { "targetSentinel", target.Sentinel }
            };
        }

        private static Dictionary<string, object> NullOwner()
        {
            var failure = "none";
            try { ((StoreOwner)null).SetAfterTouch(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "null-owner" }, { "failure", failure }
            };
        }

        private static Dictionary<string, object> Invoke(int counter, int mode, bool flag)
        {
            var first = new StoreTarget { Flag = flag, Neighbor = true, Sentinel = 17 };
            var second = new StoreTarget { Flag = !flag, Neighbor = false, Sentinel = 53 };
            var owner = new ProbeOwner
            {
                Selected = mode == 0 ? null : mode == 2 ? second : first,
                Other = mode == 3 ? first : second,
                Counter = counter, Sentinel = 97
            };
            var failure = "none";
            try { owner.SetAfterTouch(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", "invoke" }, { "counterBefore", counter },
                { "mode", mode }, { "flagBefore", flag }, { "failure", failure },
                { "counterAfter", owner.Counter },
                { "currentSameFirst", ReferenceEquals(owner.Selected, first) },
                { "currentSameSecond", ReferenceEquals(owner.Selected, second) },
                { "otherSameFirst", ReferenceEquals(owner.Other, first) },
                { "otherSameSecond", ReferenceEquals(owner.Other, second) },
                { "firstFlagAfter", first.Flag }, { "secondFlagAfter", second.Flag },
                { "firstNeighbor", first.Neighbor }, { "secondNeighbor", second.Neighbor },
                { "firstSentinel", first.Sentinel }, { "secondSentinel", second.Sentinel },
                { "ownerSentinel", owner.Sentinel }
            };
        }

        private static Dictionary<string, object> RepeatWithNewCurrent()
        {
            var first = new StoreTarget { Neighbor = true, Sentinel = 17 };
            var second = new StoreTarget { Neighbor = false, Sentinel = 53 };
            var owner = new ProbeOwner
            {
                Selected = first, Other = second,
                Counter = int.MaxValue, Sentinel = 97
            };
            owner.SetAfterTouch();
            var afterFirst = owner.Counter;
            owner.Selected = second;
            owner.SetAfterTouch();
            return new Dictionary<string, object>
            {
                { "kind", "repeat" }, { "afterFirst", afterFirst },
                { "afterSecond", owner.Counter },
                { "firstFlag", first.Flag }, { "secondFlag", second.Flag },
                { "currentSameSecond", ReferenceEquals(owner.Selected, second) },
                { "otherSameSecond", ReferenceEquals(owner.Other, second) },
                { "firstNeighbor", first.Neighbor }, { "secondNeighbor", second.Neighbor },
                { "firstSentinel", first.Sentinel }, { "secondSentinel", second.Sentinel },
                { "ownerSentinel", owner.Sentinel }
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
