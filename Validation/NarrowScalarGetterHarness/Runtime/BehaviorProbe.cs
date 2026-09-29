using System;
using System.Collections.Generic;
using System.IO;
using NarrowScalarGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var values = new List<int>();
            for (var value = 0; value < 256; value++)
                values.Add(value);
            values.AddRange(new[] { 256, 257, 32766, 32767, 32768, 32769, 65534, 65535 });
            var observations = new List<object>();
            for (var operation = 0; operation < 8; operation++)
            foreach (var value in values)
            {
                var state = new NarrowScalars
                {
                    SignedByte = unchecked((sbyte)value),
                    UnsignedByte = unchecked((byte)(value * 17 + 3)),
                    SignedWord = unchecked((short)value),
                    UnsignedWord = unchecked((ushort)(value ^ 0x8001)),
                    Neighbor = 0xfedcba98u
                };
                long result;
                if (operation == 0) result = state.ReadSignedByte();
                else if (operation == 1) result = state.ReadUnsignedByte();
                else if (operation == 2) result = state.ReadSignedWord();
                else if (operation == 3) result = state.ReadUnsignedWord();
                else if (operation == 4) result = state.WidenSignedByte();
                else if (operation == 5) result = state.WidenUnsignedByte();
                else if (operation == 6) result = state.WidenSignedWord();
                else result = state.WidenUnsignedWord();
                observations.Add(new Dictionary<string, object>
                {
                    { "operation", operation }, { "input", value }, { "result", result },
                    { "signedByteAfter", (int)state.SignedByte },
                    { "unsignedByteAfter", (int)state.UnsignedByte },
                    { "signedWordAfter", (int)state.SignedWord },
                    { "unsignedWordAfter", (int)state.UnsignedWord },
                    { "neighborAfter", state.Neighbor }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "stage", stage },
                { "platform", Application.platform.ToString() },
                { "profile", "narrow-scalar-getter" }, { "observations", observations }
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
