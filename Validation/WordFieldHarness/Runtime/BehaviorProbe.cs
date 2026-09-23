using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WordFieldFixture;

namespace RecoveryValidation
{
    // Independent driver: none of these methods belong to the recovery scope.
    public static class BehaviorProbe
    {
        private const int PatternCount = 65536;

        public static void Write(string path, string stage)
        {
            var fresh = new WordState();
            var initialFields = new List<object>
            {
                Field("SignedValue", "System.Int16", fresh.SignedValue),
                Field("UnsignedValue", "System.UInt16", fresh.UnsignedValue),
                Field("CharacterValue", "System.Char", fresh.CharacterValue)
            };
            var predicates = new List<object>
            {
                Observe("IsShortZero", "SignedValue", "System.Int16",
                    (state, value) => state.SignedValue = unchecked((short)value), state => state.IsShortZero()),
                Observe("IsUShortZero", "UnsignedValue", "System.UInt16",
                    (state, value) => state.UnsignedValue = (ushort)value, state => state.IsUShortZero()),
                Observe("IsCharZero", "CharacterValue", "System.Char",
                    (state, value) => state.CharacterValue = (char)value, state => state.IsCharZero())
            };
            WordState missing = null;
            var nullReceivers = new List<object>
            {
                ObserveNull("IsShortZero", () => missing.IsShortZero()),
                ObserveNull("IsUShortZero", () => missing.IsUShortZero()),
                ObserveNull("IsCharZero", () => missing.IsCharZero())
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "word-fields" }, { "patternCount", PatternCount },
                { "bitEncoding", "unsigned16-pattern-index-lsb-first" }, { "initialFields", initialFields },
                { "predicates", predicates }, { "nullReceivers", nullReceivers }
            }));
        }

        private static object Field(string name, string managedType, int value)
        {
            return new Dictionary<string, object> { { "field", name }, { "managedType", managedType }, { "value", value } };
        }

        private static object Observe(string member, string inputField, string inputType,
            Action<WordState, int> assign, Func<WordState, bool> predicate)
        {
            var state = new WordState();
            var results = new byte[PatternCount / 8];
            var signedUnchanged = new byte[PatternCount / 8];
            var unsignedUnchanged = new byte[PatternCount / 8];
            var characterUnchanged = new byte[PatternCount / 8];
            for (var pattern = 0; pattern < PatternCount; pattern++)
            {
                state.SignedValue = -123;
                state.UnsignedValue = 45678;
                state.CharacterValue = '\u5A5A';
                assign(state, pattern);
                var signedBefore = state.SignedValue;
                var unsignedBefore = state.UnsignedValue;
                var characterBefore = state.CharacterValue;
                var result = predicate(state);
                Record(results, pattern, result);
                Record(signedUnchanged, pattern, state.SignedValue == signedBefore);
                Record(unsignedUnchanged, pattern, state.UnsignedValue == unsignedBefore);
                Record(characterUnchanged, pattern, state.CharacterValue == characterBefore);
            }
            return new Dictionary<string, object>
            {
                { "member", member }, { "inputField", inputField }, { "inputType", inputType },
                { "zeroResults", Convert.ToBase64String(results) },
                { "unchangedFields", new Dictionary<string, object>
                    {
                        { "SignedValue", Convert.ToBase64String(signedUnchanged) },
                        { "UnsignedValue", Convert.ToBase64String(unsignedUnchanged) },
                        { "CharacterValue", Convert.ToBase64String(characterUnchanged) }
                    }
                }
            };
        }

        private static void Record(byte[] bits, int pattern, bool value)
        {
            if (value)
                bits[pattern >> 3] |= (byte)(1 << (pattern & 7));
        }

        private static object ObserveNull(string member, Func<bool> predicate)
        {
            var exceptionType = "none";
            try { predicate(); }
            catch (Exception exception) { exceptionType = exception.GetType().FullName; }
            return new Dictionary<string, object> { { "member", member }, { "exception", exceptionType } };
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
