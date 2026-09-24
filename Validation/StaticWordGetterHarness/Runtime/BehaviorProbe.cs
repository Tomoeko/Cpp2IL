using System;
using System.Collections.Generic;
using System.IO;
using StaticWordGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is independent of the two selected recovery methods.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "initial");

            StaticWordState.Neighbor = 101;
            StaticWordState.Signed = int.MinValue;
            Record(observations, "signed-minimum");
            Record(observations, "signed-repeat");

            StaticWordState.Neighbor = -202;
            StaticWordState.Unsigned = uint.MaxValue;
            Record(observations, "unsigned-maximum");

            StaticWordState.Signed = int.MaxValue;
            Record(observations, "signed-maximum");

            StaticWordState.Neighbor = 303;
            StaticWordState.Unsigned = 0x80000000u;
            Record(observations, "unsigned-high-bit");

            StaticWordState.Signed = -1;
            StaticWordState.Unsigned = 1;
            Record(observations, "mixed");

            StaticWordState.Neighbor = -404;
            StaticWordState.Signed = 0;
            Record(observations, "signed-zero");

            StaticWordState.Unsigned = 0;
            Record(observations, "unsigned-zero");
            Record(observations, "final-repeat");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "static-word-getter" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind)
        {
            var neighborBefore = StaticWordState.Neighbor;
            var firstSigned = StaticWordState.ReadSigned();
            var firstUnsigned = StaticWordState.ReadUnsigned();
            var secondSigned = StaticWordState.ReadSigned();
            var secondUnsigned = StaticWordState.ReadUnsigned();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "signed", firstSigned }, { "unsigned", firstUnsigned },
                { "signedAgain", secondSigned }, { "unsignedAgain", secondUnsigned },
                { "storedSigned", StaticWordState.Signed },
                { "storedUnsigned", StaticWordState.Unsigned },
                { "neighborBefore", neighborBefore },
                { "neighborAfter", StaticWordState.Neighbor }
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
