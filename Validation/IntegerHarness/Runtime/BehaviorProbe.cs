using System;
using System.Collections.Generic;
using System.IO;
using IntegerFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is an independent validation oracle, outside the selected recovery assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var values32 = new uint[] { 0, 1, int.MaxValue, 0x80000000U, uint.MaxValue };
            foreach (var left in values32)
            {
                foreach (var right in values32)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "width", 32 }, { "left", left }, { "right", right },
                        { "less", IntegerComparisons.Less32(left, right) },
                        { "greater", IntegerComparisons.Greater32(left, right) },
                        { "lessOrEqual", IntegerComparisons.LessOrEqual32(left, right) },
                        { "greaterOrEqual", IntegerComparisons.GreaterOrEqual32(left, right) }
                    });
                }
            }
            var values64 = new ulong[] { 0, 1, long.MaxValue, 0x8000000000000000UL, ulong.MaxValue };
            foreach (var left in values64)
            {
                foreach (var right in values64)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "width", 64 }, { "left", left }, { "right", right },
                        { "less", IntegerComparisons.Less64(left, right) },
                        { "greater", IntegerComparisons.Greater64(left, right) },
                        { "lessOrEqual", IntegerComparisons.LessOrEqual64(left, right) },
                        { "greaterOrEqual", IntegerComparisons.GreaterOrEqual64(left, right) }
                    });
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "integers" }, { "observations", observations }
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
