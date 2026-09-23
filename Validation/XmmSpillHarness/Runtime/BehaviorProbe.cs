using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using XmmSpillFixture;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, -17f, 1f, 2f, 3f);
            Record(observations, 0f, 0f, 0f, 0f);
            Record(observations, 1f, 2f, 3f, 4f);
            Record(observations, 17f, -17f, 17f, -17f);
            Record(observations, 65536f, 1f, -65536f, 2f);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "xmm-spill" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, float first, float second, float third, float fourth)
        {
            var values = new FloatPack { First = first, Second = second, Third = third, Fourth = fourth };
            var returned = FloatAcrossCall.Sum(values);
            observations.Add(new Dictionary<string, object>
            {
                { "firstBits", Bits(first) }, { "secondBits", Bits(second) },
                { "thirdBits", Bits(third) }, { "fourthBits", Bits(fourth) },
                { "resultBits", Bits(returned) }
            });
        }

        private static string Bits(float value)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8", CultureInfo.InvariantCulture);
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
