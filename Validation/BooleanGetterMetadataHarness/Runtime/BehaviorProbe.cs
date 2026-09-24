using System;
using System.Collections.Generic;
using System.IO;
using BooleanGetterMetadataFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var value in new[] { false, true })
            {
                var sharedNeighbor = new object();
                var ownNeighbor = value ? sharedNeighbor : new object();
                var inheritedWord = value ? 0x102030405L : -0x102030405L;
                var genericOwner = new GenericFieldOwner
                {
                    InheritedWord = new IntPtr(inheritedWord),
                    Value = value,
                    NeighborReference = ownNeighbor
                };
                var partner = new GenericFieldOwner
                {
                    Value = !value,
                    NeighborReference = sharedNeighbor
                };
                var genericResult = genericOwner.ReadValue;
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "generic-owner" }, { "valueBefore", value },
                    { "result", genericResult }, { "valueAfter", genericOwner.Value },
                    { "inheritedWordBefore", inheritedWord },
                    { "inheritedWordAfter", genericOwner.InheritedWord.ToInt64() },
                    { "ownNeighborSame", ReferenceEquals(genericOwner.NeighborReference, ownNeighbor) },
                    { "neighborAliasesPartner", ReferenceEquals(genericOwner.NeighborReference, partner.NeighborReference) },
                    { "partnerValueAfter", partner.Value },
                    { "partnerNeighborSame", ReferenceEquals(partner.NeighborReference, sharedNeighbor) }
                });

                var baseOwner = new VirtualBooleanBase { BaseValue = value };
                var baseResult = baseOwner.ReadValue;
                observations.Add(VirtualRow("base", value, null, baseResult,
                    baseOwner.BaseValue, null));

                var overrideOwner = new VirtualBooleanOverride
                {
                    BaseValue = !value,
                    OverrideValue = value
                };
                VirtualBooleanBase baseReference = overrideOwner;
                var dispatchedResult = baseReference.ReadValue;
                observations.Add(VirtualRow("base-reference-to-override", !value, value,
                    dispatchedResult, overrideOwner.BaseValue, overrideOwner.OverrideValue));
                var directResult = overrideOwner.ReadValue;
                observations.Add(VirtualRow("direct-override", !value, value,
                    directResult, overrideOwner.BaseValue, overrideOwner.OverrideValue));
            }

            GenericFieldOwner missingGeneric = null;
            VirtualBooleanBase missingBase = null;
            VirtualBooleanOverride missingOverride = null;
            RecordNull(observations, "generic-owner", () => { var ignored = missingGeneric.ReadValue; });
            RecordNull(observations, "virtual-base", () => { var ignored = missingBase.ReadValue; });
            RecordNull(observations, "virtual-override", () => { var ignored = missingOverride.ReadValue; });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "boolean-getter-metadata" },
                { "observations", observations }
            }));
        }

        private static object VirtualRow(string dispatch, bool baseBefore, bool? overrideBefore,
            bool result, bool baseAfter, bool? overrideAfter) => new Dictionary<string, object>
        {
            { "kind", "virtual" }, { "dispatch", dispatch },
            { "baseBefore", baseBefore }, { "overrideBefore", overrideBefore },
            { "result", result }, { "baseAfter", baseAfter },
            { "overrideAfter", overrideAfter }
        };

        private static void RecordNull(List<object> observations, string owner, Action action)
        {
            var exception = "none";
            try { action(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "owner", owner }, { "exception", exception }
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
