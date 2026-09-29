using System;
using System.Collections.Generic;
using System.IO;
using NestedByteFieldReadFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshCell = new ByteCell();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default-cell" },
                { "value", (int)freshCell.Value },
                { "neighbor", freshCell.Neighbor },
                { "referenceNull", freshCell.Reference == null }
            });

            var freshOwner = new ByteOwner();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default-owner" },
                { "childNull", freshOwner.Child == null },
                { "referenceNull", freshOwner.Reference == null },
                { "neighbor", freshOwner.Neighbor }
            });

            ByteOwner missingOwner = null;
            var missingOwnerFailure = "none";
            try { missingOwner.ReadUnsigned(); }
            catch (Exception error) { missingOwnerFailure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null-owner" }, { "failure", missingOwnerFailure }
            });

            var ownerReference = new object();
            var missingChild = new ByteOwner
            {
                Child = null, Reference = ownerReference, Neighbor = 913
            };
            var missingChildFailure = "none";
            try { missingChild.ReadUnsigned(); }
            catch (Exception error) { missingChildFailure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null-child" }, { "failure", missingChildFailure },
                { "childNull", missingChild.Child == null },
                { "referenceSame", ReferenceEquals(ownerReference, missingChild.Reference) },
                { "neighbor", missingChild.Neighbor }
            });

            for (var value = 0; value <= byte.MaxValue; value++)
                RecordValue(observations, value);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "nested-byte-field-read" },
                { "observations", observations }
            }));
        }

        private static void RecordValue(List<object> observations, int value)
        {
            var cellReference = new object();
            var ownerReference = new object();
            var aliasReference = new object();
            var cell = new ByteCell
            {
                Value = (byte)value, Neighbor = -701, Reference = cellReference
            };
            var owner = new ByteOwner
            {
                Child = cell, Reference = ownerReference, Neighbor = 913
            };
            var alias = new ByteOwner
            {
                Child = cell, Reference = aliasReference, Neighbor = -31
            };
            int? first = null;
            int? second = null;
            int? aliasResult = null;
            var failure = "none";
            try
            {
                first = owner.ReadUnsigned();
                second = owner.ReadUnsigned();
                aliasResult = alias.ReadUnsigned();
            }
            catch (Exception error) { failure = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", "value" }, { "input", value }, { "failure", failure },
                { "first", first }, { "second", second }, { "aliasResult", aliasResult },
                { "cellValue", (int)cell.Value }, { "cellNeighbor", cell.Neighbor },
                { "cellReferenceSame", ReferenceEquals(cell.Reference, cellReference) },
                { "ownerChildSame", ReferenceEquals(owner.Child, cell) },
                { "ownerReferenceSame", ReferenceEquals(owner.Reference, ownerReference) },
                { "ownerNeighbor", owner.Neighbor },
                { "aliasChildSame", ReferenceEquals(alias.Child, cell) },
                { "aliasReferenceSame", ReferenceEquals(alias.Reference, aliasReference) },
                { "aliasNeighbor", alias.Neighbor }
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
