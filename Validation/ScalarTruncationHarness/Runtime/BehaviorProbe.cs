using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ScalarTruncationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // Input construction and result formatting belong to the driver, not the recovered assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var magnitudes = new string[]
            {
                "0000000000000000", "0000000000000001", "000fffffffffffff", "0010000000000000",
                "3fdfffffffffffff", "3fe0000000000000", "3fefffffffffffff", "3ff0000000000000",
                "3ff0000000000001", "3ff8000000000000", "3fffffffffffffff", "4004000000000000",
                "41dfffffffa00000", "41dfffffffc00000", "41dfffffffe00000", "41dfffffffffffff",
                "41e0000000000000", "41e0000000000001", "41e0000000100000", "41e0000000200000",
                "41e0000000200001", "43dffffffffffffe", "43dfffffffffffff", "43e0000000000000",
                "43e0000000000001", "43e0000000000002", "433fffffffffffff", "4340000000000000",
                "4340000000000001", "7fefffffffffffff", "7ff0000000000000", "7ff8000000000001",
                "7ffb123456789abc"
            };
            var observations = new List<object>();
            foreach (var magnitude in magnitudes)
            {
                var magnitudeBits = ulong.Parse(magnitude, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
                for (var sign = 0; sign < 2; sign++)
                {
                    var inputBits = magnitudeBits | ((ulong)sign << 63);
                    var value = BitConverter.Int64BitsToDouble(unchecked((long)inputBits));
                    observations.Add(new Dictionary<string, object>
                    {
                        { "inputBits", inputBits.ToString("x16", CultureInfo.InvariantCulture) },
                        { "int32Bits", unchecked((uint)ScalarTruncation.ToInt32(value)).ToString("x8", CultureInfo.InvariantCulture) },
                        { "int64Bits", unchecked((ulong)ScalarTruncation.ToInt64(value)).ToString("x16", CultureInfo.InvariantCulture) }
                    });
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-truncation" }, { "observations", observations }
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
