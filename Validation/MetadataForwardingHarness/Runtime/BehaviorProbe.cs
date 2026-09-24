using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MetadataForwardingFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var first = new object();
            var second = new object();
            Reset(first);
            var observations = new List<object> { DeclarationFacts() };
            Record(observations, "initial", null, first, second);
            Record(observations, "cold-null", () => Forwarder.Forward(), first, second);

            SharedState.Current = first;
            Record(observations, "first-reference", () => Forwarder.Forward(), first, second);
            Record(observations, "repeat-reference", () => Forwarder.Forward(), first, second);

            SharedState.Current = first;
            Preparation.Replacement = second;
            Preparation.ReplaceNext = true;
            Record(observations, "prepare-mutation", () => Forwarder.Forward(int.MinValue),
                first, second);
            Record(observations, "signed-maximum", () => Forwarder.Forward(int.MaxValue),
                first, second);
            Record(observations, "signed-negative", () => Forwarder.Forward(-17), first, second);

            SharedState.Current = null;
            Record(observations, "null-two-argument", () => Forwarder.Forward(-1), first, second);

            SharedState.Current = second;
            Preparation.Replacement = first;
            Preparation.ReplaceNext = true;
            Preparation.ThrowNext = true;
            Record(observations, "prepare-throw", () => Forwarder.Forward(17), first, second);
            Preparation.ThrowNext = false;
            Record(observations, "after-throw", () => Forwarder.Forward(), first, second);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "metadata-forwarding" },
                { "observations", observations }
            }));
        }

        private static void Reset(object neighbor)
        {
            SharedState.Current = null;
            SharedState.Neighbor = neighbor;
            Preparation.Calls = 0;
            Preparation.Clock = 0;
            Preparation.LastOrder = 0;
            Preparation.ThrowNext = false;
            Preparation.ReplaceNext = false;
            Preparation.Replacement = null;
            Receiver.Calls = 0;
            Receiver.LastOrder = 0;
            Receiver.LastPrepareCalls = 0;
            Receiver.LastNumber = 0;
            Receiver.LastReference = null;
        }

        private static object DeclarationFacts()
        {
            var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var forwarders = typeof(Forwarder).GetMethods(flags);
            var receivers = typeof(Receiver).GetMethods(flags);
            var prepare = typeof(Preparation).GetMethod("Prepare", flags);
            var stateFields = typeof(SharedState).GetFields(flags);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "forwardersExact", forwarders.Length == 2 &&
                    HasMethod(typeof(Forwarder), "Forward", typeof(bool), Type.EmptyTypes) &&
                    HasMethod(typeof(Forwarder), "Forward", typeof(bool), new[] { typeof(int) }) },
                { "receiversExact", receivers.Length == 2 &&
                    HasMethod(typeof(Receiver), "Receive", typeof(bool), new[] { typeof(object) }) &&
                    HasMethod(typeof(Receiver), "Receive", typeof(bool),
                        new[] { typeof(object), typeof(int) }) },
                { "prepareExact", prepare != null && prepare.ReturnType == typeof(void) &&
                    prepare.GetParameters().Length == 0 &&
                    (prepare.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining) != 0 },
                { "stateFieldsExact", stateFields.Length == 2 &&
                    HasField("Current") && HasField("Neighbor") },
                { "separateTypes", typeof(Forwarder) != typeof(SharedState) &&
                    typeof(Preparation) != typeof(Receiver) },
                { "noStaticConstructors", typeof(Forwarder).TypeInitializer == null &&
                    typeof(SharedState).TypeInitializer == null &&
                    typeof(Preparation).TypeInitializer == null &&
                    typeof(Receiver).TypeInitializer == null }
            };
        }

        private static bool HasMethod(Type owner, string name, Type result, Type[] parameters)
        {
            var method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.Static |
                BindingFlags.DeclaredOnly, null, parameters, null);
            return method != null && method.ReturnType == result &&
                !method.IsGenericMethod &&
                (method.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining) != 0;
        }

        private static bool HasField(string name)
        {
            var field = typeof(SharedState).GetField(name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            return field != null && field.FieldType == typeof(object) && !field.IsInitOnly;
        }

        private static void Record(List<object> observations, string kind, Func<bool> invoke,
            object first, object second)
        {
            object result = null;
            var exception = "none";
            try
            {
                if (invoke != null)
                    result = invoke();
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "result", result },
                { "exception", exception },
                { "prepareCalls", Preparation.Calls },
                { "receiverCalls", Receiver.Calls },
                { "clock", Preparation.Clock },
                { "prepareOrder", Preparation.LastOrder },
                { "receiverOrder", Receiver.LastOrder },
                { "prepareCallsAtReceiver", Receiver.LastPrepareCalls },
                { "lastNumber", Receiver.LastNumber },
                { "current", Identity(SharedState.Current, first, second) },
                { "lastArgument", Identity(Receiver.LastReference, first, second) },
                { "neighborPreserved", ReferenceEquals(SharedState.Neighbor, first) },
                { "replacePending", Preparation.ReplaceNext },
                { "throwPending", Preparation.ThrowNext }
            });
        }

        private static string Identity(object value, object first, object second)
        {
            if (ReferenceEquals(value, first))
                return "first";
            if (ReferenceEquals(value, second))
                return "second";
            return value == null ? "null" : "other";
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
