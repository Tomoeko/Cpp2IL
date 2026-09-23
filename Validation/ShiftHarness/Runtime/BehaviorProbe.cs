using System;
using System.Collections.Generic;
using System.IO;
using ShiftFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is an independent validation oracle, outside the selected recovery assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var counts = new int[] { -65, -64, -33, -32, -1, 0, 1, 31, 32, 33, 63, 64, 65 };
            var values32 = new uint[] { 0, 1, int.MaxValue, 0x80000000U, uint.MaxValue };
            foreach (var value in values32)
            {
                foreach (var count in counts)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "width", 32 }, { "value", value }, { "count", count },
                        { "arithmetic", IntegerShifts.Arithmetic32(unchecked((int)value), count) },
                        { "logical", IntegerShifts.Logical32(value, count) }
                    });
                }
            }
            var values64 = new ulong[] { 0, 1, long.MaxValue, 0x8000000000000000UL, ulong.MaxValue };
            foreach (var value in values64)
            {
                foreach (var count in counts)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "width", 64 }, { "value", value }, { "count", count },
                        { "arithmetic", IntegerShifts.Arithmetic64(unchecked((long)value), count) },
                        { "logical", IntegerShifts.Logical64(value, count) }
                    });
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "shifts" }, { "observations", observations }
            }));
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
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
