using System;
using System.Collections.Generic;
using System.IO;
using NestedBooleanStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>
            {
                Observe("true", CreateOwner(new BooleanTarget { Flag = true, Neighbor = 53 }, -71)),
                Observe("false", CreateOwner(new BooleanTarget { Flag = false, Neighbor = -53 }, 71)),
                Observe("target-null", CreateOwner(null, -71)),
                Observe("owner-null", null)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "nested-boolean-store" },
                { "observations", observations }
            }));
        }

        private static BooleanOwner CreateOwner(BooleanTarget target, long neighbor)
        {
            return new BooleanOwner
            {
                Pad00 = 1, Pad01 = 2, Pad02 = 3, Pad03 = 4, Pad04 = 5,
                Pad05 = 6, Pad06 = 7, Pad07 = 8, Pad08 = 9, Pad09 = 10,
                Pad10 = 11, Pad11 = 12, Pad12 = 13, Pad13 = 14, Pad14 = 15,
                Target = target,
                Neighbor = neighbor
            };
        }

        private static long PaddingSum(BooleanOwner owner)
        {
            return owner.Pad00 + owner.Pad01 + owner.Pad02 + owner.Pad03 + owner.Pad04 +
                owner.Pad05 + owner.Pad06 + owner.Pad07 + owner.Pad08 + owner.Pad09 +
                owner.Pad10 + owner.Pad11 + owner.Pad12 + owner.Pad13 + owner.Pad14;
        }

        private static object Observe(string kind, BooleanOwner owner)
        {
            var alias = owner == null ? null : owner.Target;
            object flagBefore = alias == null ? null : (object)alias.Flag;
            object targetNeighborBefore = alias == null ? null : (object)alias.Neighbor;
            object ownerNeighborBefore = owner == null ? null : (object)owner.Neighbor;
            object paddingBefore = owner == null ? null : (object)PaddingSum(owner);
            var exception = "none";
            try { owner.ClearFlag(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "flagBefore", flagBefore },
                { "flagAfter", alias == null ? null : (object)alias.Flag },
                { "exception", exception },
                { "sameTargetReference", owner == null ? null : (object)ReferenceEquals(alias, owner.Target) },
                { "targetNeighborBefore", targetNeighborBefore },
                { "targetNeighborAfter", alias == null ? null : (object)alias.Neighbor },
                { "ownerNeighborBefore", ownerNeighborBefore },
                { "ownerNeighborAfter", owner == null ? null : (object)owner.Neighbor },
                { "paddingBefore", paddingBefore },
                { "paddingAfter", owner == null ? null : (object)PaddingSum(owner) }
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
