using System;
using System.Collections.Generic;
using System.IO;
using CallResultBooleanStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public class DerivedFlagCell : FlagCell
    {
        public int DerivedNeighbor;
    }

    public class DerivedFlagOwner : FlagOwner
    {
        public int DerivedNeighbor;
    }

    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new FlagOwner();
            var freshCell = new FlagCell();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", freshOwner != null },
                { "cellCreated", freshCell != null },
                { "cellIsNull", freshOwner.Cell == null },
                { "enabled", freshCell.Enabled },
                { "cellNeighbor", freshCell.Neighbor },
                { "touchCount", freshOwner.TouchCount },
                { "ownerNeighbor", freshOwner.Neighbor }
            });

            var cell = new FlagCell { Enabled = false, Neighbor = 29 };
            var owner = new FlagOwner { Cell = cell, Neighbor = 13 };
            Record(observations, "enable", owner, cell, true);
            owner.TouchCount = 0;
            Record(observations, "disable", owner, cell, false);

            var alias = new FlagOwner { Cell = cell, Neighbor = -17 };
            alias.TouchCount = 0;
            Record(observations, "shared-enable", alias, cell, true);
            owner.TouchCount = 0;
            Record(observations, "shared-disable", owner, cell, false);

            var absent = new FlagOwner { Cell = null, Neighbor = 31 };
            Record(observations, "null-cell-enable", absent, cell, true);
            absent.TouchCount = 0;
            Record(observations, "null-cell-disable", absent, cell, false);

            Record(observations, "null-owner", null, cell, true);

            var derivedCell = new DerivedFlagCell
            {
                Enabled = false, Neighbor = 41, DerivedNeighbor = 47
            };
            var derivedOwner = new DerivedFlagOwner
            {
                Cell = derivedCell, Neighbor = 59, DerivedNeighbor = 61
            };
            Record(observations, "derived", derivedOwner, derivedCell, true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "call-result-boolean-store" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            FlagOwner owner, FlagCell witness, bool enable)
        {
            var before = witness.Enabled;
            var exception = "none";
            try
            {
                if (enable) owner.Enable();
                else owner.Disable();
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "ownerIsNull", owner == null },
                { "cellIsNull", owner == null ? null : (object)(owner.Cell == null) },
                { "cellSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Cell, witness) },
                { "enabledBefore", before },
                { "enabledAfter", witness.Enabled },
                { "cellNeighborAfter", witness.Neighbor },
                { "touchCountAfter", owner == null ? null : (object)owner.TouchCount },
                { "ownerNeighborAfter", owner == null ? null : (object)owner.Neighbor },
                { "derivedCellNeighbor", witness is DerivedFlagCell derivedCell ?
                    (object)derivedCell.DerivedNeighbor : null },
                { "derivedOwnerNeighbor", owner is DerivedFlagOwner derivedOwner ?
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
