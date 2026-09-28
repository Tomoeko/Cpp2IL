using System;
using System.Collections.Generic;
using System.IO;
using RegisterZeroExtensionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly ushort[] WordValues =
        {
            0, 1, 0x7f, 0x80, 0xff, 0x100, 0x7fff, 0x8000, 0xff00, 0xffff
        };
        private static readonly int[] Biases = { int.MinValue, -1, 0, 1, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var value = 0; value < 256; value++)
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "width", 8 },
                    { "input", value },
                    { "result", ZeroExtensions.WidenByte((byte)value) }
                });
                foreach (var bias in Biases)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "width", 8 },
                        { "input", value },
                        { "bias", bias },
                        { "result", ZeroExtensions.AddByteAndBias((byte)value, bias) }
                    });
                }
            }

            foreach (var value in WordValues)
            {
                observations.Add(new Dictionary<string, object>
                {
                    { "width", 16 },
                    { "input", (int)value },
                    { "result", ZeroExtensions.WidenWord(value) }
                });
                foreach (var bias in Biases)
                {
                    observations.Add(new Dictionary<string, object>
                    {
                        { "width", 16 },
                        { "input", (int)value },
                        { "bias", bias },
                        { "result", ZeroExtensions.AddWordAndBias(value, bias) }
                    });
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "register-zero-extension" },
                { "observations", observations }
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
