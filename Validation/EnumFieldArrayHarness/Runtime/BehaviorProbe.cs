using System;
using System.Collections.Generic;
using System.IO;
using EnumFieldArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var kind in new[] { "null-owner", "null-array", "empty", "single", "double", "mixed" })
            {
                var length = kind == "single" ? 1 : kind == "double" ? 2 : kind == "mixed" ? 5 : 0;
                RecordBoth(observations, kind, length, "read-first", null, 0);
                RecordBoth(observations, kind, length, "read-fixed", null, 2);
                foreach (var index in new[] { int.MinValue, -1, 0, 1, length - 1, length, int.MaxValue })
                    RecordBoth(observations, kind, length, "read-at", index, index);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "enum-field-array" },
                { "observations", observations }
            }));
        }

        private static void RecordBoth(List<object> observations, string kind, int length,
            string operation, int? requestedIndex, int accessIndex)
        {
            var missingOwner = kind == "null-owner";
            var missingArray = kind == "null-array";
            var signed = missingOwner ? null : new SignedReader
            {
                Before = -53, Values = missingArray ? null : SignedValues(length), After = 59
            };
            var signedValues = signed == null ? null : signed.Values;
            var signedAlias = missingOwner ? null : new SignedReader
            {
                Before = 61, Values = signedValues, After = -67
            };
            observations.Add(Observe(kind, "signed", operation, requestedIndex,
                () => (long)(int)(operation == "read-first" ? signed.ReadFirst() :
                    operation == "read-fixed" ? signed.ReadFixed() : signed.ReadAt(accessIndex)),
                SignedNumbers(signedValues), () => SignedNumbers(signed.Values),
                () => SignedNumbers(signedAlias.Values),
                () => ReferenceEquals(signedValues, signed.Values),
                () => signedValues != null && ReferenceEquals(signedAlias.Values, signedValues),
                () => signed.Before, () => null, () => signed.After,
                () => signedAlias.Before, () => signedAlias.After, !missingOwner));

            var unsigned = missingOwner ? null : new UnsignedReader
            {
                Before = -53, Spacer = -71, Values = missingArray ? null : UnsignedValues(length), After = 59
            };
            var unsignedValues = unsigned == null ? null : unsigned.Values;
            var unsignedAlias = missingOwner ? null : new UnsignedReader
            {
                Before = 61, Spacer = 73, Values = unsignedValues, After = -67
            };
            observations.Add(Observe(kind, "unsigned", operation, requestedIndex,
                () => (long)(uint)(operation == "read-first" ? unsigned.ReadFirst() :
                    operation == "read-fixed" ? unsigned.ReadFixed() : unsigned.ReadAt(accessIndex)),
                UnsignedNumbers(unsignedValues), () => UnsignedNumbers(unsigned.Values),
                () => UnsignedNumbers(unsignedAlias.Values),
                () => ReferenceEquals(unsignedValues, unsigned.Values),
                () => unsignedValues != null && ReferenceEquals(unsignedAlias.Values, unsignedValues),
                () => unsigned.Before, () => unsigned.Spacer, () => unsigned.After,
                () => unsignedAlias.Before, () => unsignedAlias.After, !missingOwner));
        }

        private static object Observe(string kind, string element, string operation, int? index,
            Func<long> read, long[] before, Func<long[]> after, Func<long[]> aliasAfter,
            Func<bool> sameArray, Func<bool> sharesArray, Func<long> ownerBefore,
            Func<long?> spacer, Func<long> ownerAfter, Func<long> aliasBefore,
            Func<long> aliasEnd, bool hasOwner)
        {
            long? result = null;
            var exception = "none";
            string message = null;
            int? hresult = null;
            var innerException = false;
            try { result = read(); }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
                message = error.Message;
                hresult = error.HResult;
                innerException = error.InnerException != null;
            }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "element", element }, { "operation", operation }, { "index", index },
                { "result", result }, { "exception", exception }, { "message", message },
                { "hresult", hresult }, { "innerException", innerException },
                { "before", before }, { "after", hasOwner ? after() : null },
                { "aliasAfter", hasOwner ? aliasAfter() : null },
                { "sameArray", hasOwner && sameArray() },
                { "aliasSharesArray", hasOwner && sharesArray() },
                { "ownerBefore", hasOwner ? (object)ownerBefore() : null },
                { "spacer", hasOwner ? (object)spacer() : null },
                { "ownerAfter", hasOwner ? (object)ownerAfter() : null },
                { "aliasBefore", hasOwner ? (object)aliasBefore() : null },
                { "aliasAfterMarker", hasOwner ? (object)aliasEnd() : null }
            };
        }

        private static SignedTone[] SignedValues(int length)
        {
            var all = new[] { SignedTone.Negative, SignedTone.Zero, SignedTone.HighBit,
                SignedTone.Positive, (SignedTone)253 };
            var values = new SignedTone[length];
            Array.Copy(all, values, length);
            return values;
        }

        private static UnsignedTone[] UnsignedValues(int length)
        {
            var all = new[] { UnsignedTone.Maximum, UnsignedTone.Zero, UnsignedTone.HighBit,
                UnsignedTone.Positive, (UnsignedTone)253 };
            var values = new UnsignedTone[length];
            Array.Copy(all, values, length);
            return values;
        }

        private static long[] SignedNumbers(SignedTone[] values)
        {
            if (values == null) return null;
            var numbers = new long[values.Length];
            for (var index = 0; index < values.Length; index++) numbers[index] = (int)values[index];
            return numbers;
        }

        private static long[] UnsignedNumbers(UnsignedTone[] values)
        {
            if (values == null) return null;
            var numbers = new long[values.Length];
            for (var index = 0; index < values.Length; index++) numbers[index] = (uint)values[index];
            return numbers;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
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
