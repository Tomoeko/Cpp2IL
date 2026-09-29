using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ScalarZeroReturnFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var single = ScalarZeroReturns.SingleZero();
            var @double = ScalarZeroReturns.DoubleZero();
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "single" },
                    { "bits", BitConverter.ToUInt32(BitConverter.GetBytes(single), 0)
                        .ToString("x8", CultureInfo.InvariantCulture) },
                },
                new Dictionary<string, object>
                {
                    { "kind", "double" },
                    { "bits", BitConverter.ToUInt64(BitConverter.GetBytes(@double), 0)
                        .ToString("x16", CultureInfo.InvariantCulture) },
                },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "scalar-zero-return" },
                { "observations", observations },
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
