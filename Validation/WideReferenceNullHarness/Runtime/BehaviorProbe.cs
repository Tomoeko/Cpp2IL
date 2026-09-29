using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WideReferenceNullFixture;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var rows = new List<object>();
            var owner = NewOwner();
            Record(rows, "all-null", owner);
            owner.Pad01 = new object();
            Record(rows, "near-only", owner);
            owner.Pad01 = null;
            owner.First = new object();
            Record(rows, "first-only", owner);
            owner.First = null;
            owner.Second = new object();
            Record(rows, "second-only", owner);
            owner.Pad01 = new object();
            owner.First = new object();
            Record(rows, "all-values", owner);
            var shared = new object();
            owner.Pad01 = shared;
            owner.First = shared;
            owner.Second = shared;
            Record(rows, "shared-target", owner);
            owner.Pad01 = null;
            owner.First = null;
            owner.Second = null;
            Record(rows, "cleared", owner);
            Record(rows, "null-owner", null);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "wide-reference-null" },
                { "observations", rows }
            }));
        }

        private static ReferenceSlotOwner NewOwner()
        {
            return new ReferenceSlotOwner
            {
                Pad00 = new object(), Pad06 = new object(), Gap = new object()
            };
        }

        private static void Record(List<object> rows, string kind, ReferenceSlotOwner owner)
        {
            var near = owner == null ? null : owner.Pad01;
            var first = owner == null ? null : owner.First;
            var second = owner == null ? null : owner.Second;
            var pad00 = owner == null ? null : owner.Pad00;
            var pad06 = owner == null ? null : owner.Pad06;
            var gap = owner == null ? null : owner.Gap;
            bool? hasNear = null;
            bool? hasFirst = null;
            bool? isSecondNull = null;
            bool? hasNearRepeat = null;
            bool? hasFirstRepeat = null;
            bool? isSecondNullRepeat = null;
            var nearFailure = "none";
            var firstFailure = "none";
            var secondFailure = "none";
            try
            {
                hasNear = owner.HasNear;
                hasNearRepeat = owner.HasNear;
            }
            catch (Exception error) { nearFailure = error.GetType().FullName; }
            try
            {
                hasFirst = owner.HasFirst;
                hasFirstRepeat = owner.HasFirst;
            }
            catch (Exception error) { firstFailure = error.GetType().FullName; }
            try
            {
                isSecondNull = owner.IsSecondNull;
                isSecondNullRepeat = owner.IsSecondNull;
            }
            catch (Exception error) { secondFailure = error.GetType().FullName; }

            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "hasNear", hasNear }, { "hasNearRepeat", hasNearRepeat },
                { "hasFirst", hasFirst }, { "hasFirstRepeat", hasFirstRepeat },
                { "isSecondNull", isSecondNull },
                { "isSecondNullRepeat", isSecondNullRepeat },
                { "nearFailure", nearFailure }, { "firstFailure", firstFailure },
                { "secondFailure", secondFailure },
                { "nearNullBefore", owner == null ? (bool?)null : near == null },
                { "firstNullBefore", owner == null ? (bool?)null : first == null },
                { "secondNullBefore", owner == null ? (bool?)null : second == null },
                { "nearSame", owner == null ? (bool?)null : ReferenceEquals(near, owner.Pad01) },
                { "firstSame", owner == null ? (bool?)null : ReferenceEquals(first, owner.First) },
                { "secondSame", owner == null ? (bool?)null : ReferenceEquals(second, owner.Second) },
                { "pad00Same", owner == null ? (bool?)null : ReferenceEquals(pad00, owner.Pad00) },
                { "pad06Same", owner == null ? (bool?)null : ReferenceEquals(pad06, owner.Pad06) },
                { "gapSame", owner == null ? (bool?)null : ReferenceEquals(gap, owner.Gap) },
                { "allTargetsAlias", owner == null ? (bool?)null :
                    near != null && ReferenceEquals(near, first) && ReferenceEquals(first, second) }
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
