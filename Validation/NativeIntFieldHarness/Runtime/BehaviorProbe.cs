using System;
using System.Collections.Generic;
using System.IO;
using NativeIntFieldFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var box = new PointerBox
            {
                Before = -37,
                Signed = new IntPtr(-1),
                Unsigned = new UIntPtr(ulong.MaxValue),
                After = 41
            };
            Record(observations, "signed-negative", () =>
                PointerBox.ReadSigned(box).ToInt64());
            Record(observations, "unsigned-high", () =>
                unchecked((long)PointerBox.ReadUnsigned(box).ToUInt64()));
            box.Signed = IntPtr.Zero;
            box.Unsigned = UIntPtr.Zero;
            Record(observations, "signed-zero", () =>
                PointerBox.ReadSigned(box).ToInt64());
            Record(observations, "unsigned-zero", () =>
                unchecked((long)PointerBox.ReadUnsigned(box).ToUInt64()));
            box.Signed = new IntPtr(0x1234);
            box.Unsigned = new UIntPtr(0x5678u);
            Record(observations, "signed-positive", () =>
                PointerBox.ReadSigned(box).ToInt64());
            Record(observations, "unsigned-positive", () =>
                unchecked((long)PointerBox.ReadUnsigned(box).ToUInt64()));
            Record(observations, "signed-null", () =>
                PointerBox.ReadSigned(null).ToInt64());
            Record(observations, "unsigned-null", () =>
                unchecked((long)PointerBox.ReadUnsigned(null).ToUInt64()));
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "neighbors" }, { "before", box.Before }, { "after", box.After }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-int-field" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, Func<long> read)
        {
            object value = null;
            var exception = "none";
            try { value = read(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "value", value }, { "exception", exception }
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
