using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LookupGuardManagedThrowFixture;
using Neutral.LookupGuard;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var reader = new RecordReader();
            var originalCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                LookupEffects.Reset();
                Record(observations, "cold", reader, 37, "first");
                Run(observations, "warm", reader, -2, "first");
                Run(observations, "null-text", reader, 0, null);
                Run(observations, "empty-text", reader, 1, "");
                Run(observations, "alias-text", reader, 2, new string(new[] { 'a', 'b' }));
                Run(observations, "minimum-key", reader, int.MinValue, "minimum");
                Run(observations, "maximum-key", reader, int.MaxValue, "maximum");
                Run(observations, "absent-zero", reader, 0, null, absent: true);
                Run(observations, "absent-minimum", reader, int.MinValue, null, absent: true);
                Run(observations, "absent-maximum", reader, int.MaxValue, null, absent: true);
                Run(observations, "null-producer", reader, 17, null, nullProducer: true);
                Run(observations, "lookup-throw", reader, -17, null, lookupThrows: true);
                Run(observations, "post-throw", reader, 19, "reused");
                Run(observations, "absent-negative", reader, -17, null, absent: true);
                var customCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
                customCulture.NumberFormat.NegativeSign = "~";
                CultureInfo.CurrentCulture = customCulture;
                Run(observations, "absent-custom-culture", reader, -17, null, absent: true);
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                Run(observations, "null-caller", null, 23, "unread");
                Run(observations, "after-null-caller", reader, 29, "after-null");
                Run(observations, "property-value", reader, 31, "property", property: true);
                Run(observations, "property-null-text", reader, 0, null, property: true);
                Run(observations, "property-absent", reader, 0, null, absent: true, property: true);
                Run(observations, "property-null-producer", reader, 33, null,
                    nullProducer: true, property: true);
            }
            finally
            {
                CultureInfo.CurrentCulture = originalCulture;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "lookup-guard-managed-throw" },
                { "observations", observations }
            }));
        }

        private static void Run(List<object> observations, string kind, RecordReader reader,
            int key, string text, bool absent = false, bool nullProducer = false,
            bool lookupThrows = false, bool property = false)
        {
            RecordSource.Current = nullProducer ? null : new LookupService
            {
                Current = absent ? null : new TextRecord { Text = text, PropertyText = text },
                ThrowOnLookup = lookupThrows
            };
            LookupEffects.Reset();
            Record(observations, kind, reader, key, text, property);
        }

        private static void Record(List<object> observations, string kind, RecordReader reader,
            int key, string expectedText, bool property = false)
        {
            string result = null;
            string exception = "none";
            string message = null;
            string parameter = null;
            bool? sameString = null;
            try
            {
                result = property ? reader.ReadProperty(key) : reader.Read(key);
                sameString = ReferenceEquals(result, expectedText);
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
                if (error is ArgumentException argument)
                {
                    message = argument.Message;
                    parameter = argument.ParamName;
                }
                else if (error is InvalidOperationException)
                    message = error.Message;
            }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "key", key }, { "result", result },
                { "exception", exception }, { "message", message },
                { "parameter", parameter }, { "sameString", sameString },
                { "initializations", LookupEffects.Initializations },
                { "producerCalls", LookupEffects.ProducerCalls },
                { "lookupCalls", LookupEffects.LookupCalls },
                { "lastKey", LookupEffects.LastKey }
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
