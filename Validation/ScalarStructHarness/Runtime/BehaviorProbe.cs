using System;
using System.Collections.Generic;
using System.IO;
using ScalarStructFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // Independent validation driver; never supplied as part of the selected recovered assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var values32 = new uint[] { 0, 1, int.MaxValue, 0x80000000U, uint.MaxValue };
            foreach (var left in values32)
            foreach (var right in values32)
            {
                var a = new Word32 { Value = left };
                var b = new Word32 { Value = right };
                observations.Add(new Dictionary<string, object>
                {
                    { "width", 32 }, { "left", left }, { "right", right },
                    { "equal", ScalarStructs.Equal32(a, b) }, { "sum", ScalarStructs.Add32(a, b) }
                });
            }
            var values64 = new ulong[] { 0, 1, long.MaxValue, 0x8000000000000000UL, ulong.MaxValue };
            foreach (var left in values64)
            foreach (var right in values64)
            {
                var a = new Word64 { Value = left };
                var b = new Word64 { Value = right };
                observations.Add(new Dictionary<string, object>
                {
                    { "width", 64 }, { "left", left }, { "right", right },
                    { "equal", ScalarStructs.Equal64(a, b) }, { "sum", ScalarStructs.Add64(a, b) }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-structs" }, { "observations", observations }
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
