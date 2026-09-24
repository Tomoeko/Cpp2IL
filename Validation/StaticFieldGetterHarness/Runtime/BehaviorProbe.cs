using System;
using System.Collections.Generic;
using System.IO;
using StaticFieldGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "initial", null, 0, false, false);

            var first = new ReferenceHolder();
            StaticState.Pointer = new IntPtr(17);
            StaticState.Reference = first;
            StaticState.Flag = true;
            StaticState.NeighborFlag = false;
            Record(observations, "first", first, 17, true, false);

            var second = new ReferenceHolder();
            StaticState.Pointer = new IntPtr(-17);
            StaticState.Reference = second;
            StaticState.Flag = false;
            StaticState.NeighborFlag = true;
            Record(observations, "second", second, -17, false, true);

            StaticState.Pointer = new IntPtr(long.MinValue);
            StaticState.Reference = null;
            StaticState.Flag = true;
            StaticState.NeighborFlag = false;
            Record(observations, "minimum", null, long.MinValue, true, false);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "static-field-getter" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, ReferenceHolder expected,
            long pointer, bool expectedFlag, bool expectedNeighborFlag)
        {
            var returned = StaticState.ReadReference();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "pointer", StaticState.ReadPointer().ToInt64() },
                { "referenceMatches", ReferenceEquals(returned, expected) },
                { "storedPointer", StaticState.Pointer.ToInt64() },
                { "storedReferenceMatches", ReferenceEquals(StaticState.Reference, expected) },
                { "expectedPointer", pointer },
                { "flag", StaticState.ReadFlag() },
                { "storedFlag", StaticState.Flag },
                { "neighborFlag", StaticState.NeighborFlag },
                { "expectedFlag", expectedFlag },
                { "expectedNeighborFlag", expectedNeighborFlag }
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
