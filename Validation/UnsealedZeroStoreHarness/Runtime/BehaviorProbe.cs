using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnsealedZeroStoreFixture;

namespace RecoveryValidation
{
    public class DerivedStoreBox : StoreBox
    {
        public int DerivedNeighbor;
    }

    public class DerivedStoreOwner : StoreOwner
    {
        public int DerivedNeighbor;
    }

    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new StoreOwner();
            var freshBox = new StoreBox();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", freshOwner != null },
                { "boxCreated", freshBox != null },
                { "boxIsNull", freshOwner.Box == null },
                { "count", freshBox.Count },
                { "neighbor", freshBox.Neighbor },
                { "prefix", freshOwner.Prefix },
                { "suffix", freshOwner.Suffix }
            });

            var box = new StoreBox { Count = 71, Neighbor = 29 };
            var owner = new StoreOwner { Prefix = -11, Box = box, Suffix = 13 };
            Record(observations, "basic", owner, box, null);
            box.Count = int.MinValue;
            Record(observations, "repeat", owner, box, null);

            box.Count = 37;
            var alias = new StoreOwner { Prefix = -17, Box = box, Suffix = 19 };
            Record(observations, "shared", owner, box, alias);

            var absent = new StoreOwner { Prefix = -23, Box = null, Suffix = 31 };
            box.Count = 43;
            Record(observations, "null-box", absent, box, alias);
            Record(observations, "null-owner", null, box, alias);

            var derivedBox = new DerivedStoreBox
            {
                Count = -53, Neighbor = 41, DerivedNeighbor = 47
            };
            var derivedOwner = new DerivedStoreOwner
            {
                Prefix = -37, Box = derivedBox, Suffix = 59, DerivedNeighbor = 61
            };
            Record(observations, "derived", derivedOwner, derivedBox, null);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "unsealed-zero-store" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            StoreOwner owner, StoreBox witness, StoreOwner alias)
        {
            var before = witness.Count;
            var exception = "none";
            try { owner.Clear(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "ownerIsNull", owner == null },
                { "boxIsNull", owner == null ? null : (object)(owner.Box == null) },
                { "boxSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Box, witness) },
                { "countBefore", before },
                { "countAfter", witness.Count },
                { "neighborAfter", witness.Neighbor },
                { "prefixAfter", owner == null ? null : (object)owner.Prefix },
                { "suffixAfter", owner == null ? null : (object)owner.Suffix },
                { "aliasCountAfter", alias == null ? null : (object)alias.Box.Count },
                { "derivedBoxNeighbor", witness is DerivedStoreBox derivedBox ?
                    (object)derivedBox.DerivedNeighbor : null },
                { "derivedOwnerNeighbor", owner is DerivedStoreOwner derivedOwner ?
                    (object)derivedOwner.DerivedNeighbor : null }
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
