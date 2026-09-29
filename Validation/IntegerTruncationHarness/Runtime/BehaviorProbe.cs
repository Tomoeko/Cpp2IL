using System;
using System.Collections.Generic;
using System.IO;
using IntegerTruncationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var patterns = new ulong[] { 0, 1, 0x7fffffff, 0x80000000, 0xffffffff, 0x100000000,
                0x7fffffff00000000, 0x8000000000000000, 0x80000000ffffffff,
                0xffffffff00000000, 0xffffffffffffffff, 0x123456789abcdef0 };
            foreach (var operation in new[] { "lowSigned", "lowUnsigned", "highSigned", "highUnsigned", "splitSigned", "splitUnsigned" })
            foreach (var bits in patterns)
            for (var initial = 0; initial < (operation.StartsWith("split", StringComparison.Ordinal) ? 3 : 1); initial++)
            {
                var signedInitial = new[] { 0, int.MinValue, int.MaxValue }[initial];
                var unsignedInitial = new uint[] { 0, 0x80000000, uint.MaxValue }[initial];
                var state = new IntegerHalves { SignedLow = signedInitial, SignedHigh = signedInitial,
                    UnsignedLow = unsignedInitial, UnsignedHigh = unsignedInitial };
                long result = 0;
                if (operation == "lowSigned") result = IntegerHalves.LowSigned(unchecked((long)bits));
                else if (operation == "lowUnsigned") result = IntegerHalves.LowUnsigned(bits);
                else if (operation == "highSigned") result = IntegerHalves.HighSigned(unchecked((long)bits));
                else if (operation == "highUnsigned") result = IntegerHalves.HighUnsigned(bits);
                else if (operation == "splitSigned") state.SplitSigned(unchecked((long)bits));
                else state.SplitUnsigned(bits);
                observations.Add(new Dictionary<string, object>
                {
                    { "operation", operation }, { "bits", bits }, { "initial", initial }, { "result", result },
                    { "signedLow", state.SignedLow }, { "signedHigh", state.SignedHigh },
                    { "unsignedLow", state.UnsignedLow }, { "unsignedHigh", state.UnsignedHigh }
                });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "integer-truncation" }, { "observations", observations }
            }));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception failure) { Debug.LogException(failure); Application.Quit(1); }
                return;
            }
        }
    }
}
