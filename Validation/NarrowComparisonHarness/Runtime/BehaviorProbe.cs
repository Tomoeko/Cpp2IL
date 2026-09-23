using System;
using System.Collections.Generic;
using System.IO;
using NarrowComparisonFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // This driver is an independent validation oracle, outside the selected recovery assembly.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var condition in new[] { false, true })
            foreach (var initial in new[] { false, true })
            {
                var state = new ByteState { Condition = condition, Observed = initial };
                state.ObserveCondition();
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "boolean" }, { "condition", condition }, { "initial", initial },
                    { "conditionAfter", state.Condition }, { "observed", state.Observed }
                });
            }
            foreach (byte value in new byte[] { 0, 1, 127, 128, 255 })
            {
                var state = new ByteState { ByteValue = value };
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "byte" }, { "value", (int)value }, { "zero", state.IsByteZero() },
                    { "highBit", state.HasByteHighBit() }
                });
            }
            foreach (sbyte value in new sbyte[] { -128, -1, 0, 1, 127 })
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "signed" }, { "value", (int)value },
                    { "negative", new ByteState { SignedValue = value }.IsSignedNegative() }
                });
            foreach (ushort value in new ushort[] { 0, 1, 255, 256, 65535 })
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "word" }, { "value", (int)value },
                    { "zero", new ByteState { WordValue = value }.IsWordZero() }
                });
            for (var repeat = 0; repeat < 2; repeat++)
                observations.Add(new Dictionary<string, object>
                {
                    { "case", "metadata" }, { "repeat", repeat },
                    { "literal", MetadataCases.ReadLiteral() },
                    { "typeMatches", MetadataCases.ReadTypeToken() == typeof(ByteState) }
                });
            var initialCompleted = InitializationObserver.CompletedCount;
            var initialThrowing = InitializationObserver.ThrowingCount;
            var first = InitializationConsumers.ReadInitialized();
            var second = InitializationConsumers.ReadInitialized();
            var failures = new List<object>();
            for (var repeat = 0; repeat < 2; repeat++)
            {
                try
                {
                    InitializationConsumers.TouchThrowing();
                    failures.Add("no_exception");
                }
                catch (TypeInitializationException exception)
                {
                    failures.Add(exception.InnerException is InvalidOperationException
                        ? "TypeInitializationException/InvalidOperationException" : "unexpected_inner_exception");
                }
            }
            observations.Add(new Dictionary<string, object>
            {
                { "case", "initialization" }, { "initialCompleted", initialCompleted },
                { "initialThrowing", initialThrowing }, { "first", first }, { "second", second },
                { "completedCount", InitializationObserver.CompletedCount },
                { "throwingCount", InitializationObserver.ThrowingCount }, { "failures", failures }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "narrow-comparisons" }, { "observations", observations }
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
