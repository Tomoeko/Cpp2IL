using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GuardedSinkFixture;
using Neutral.GuardedSink;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var first = new InvalidOperationException("first");
            var second = new ArgumentException("second");
            var left = new Forwarder();
            var leftAlias = left;
            var right = new Forwarder();
            var leftNeighbor = new object();
            var rightNeighbor = new object();
            left.Neighbor = leftNeighbor;
            right.Neighbor = rightNeighbor;
            Forwarder nullOwner = null;
            var observations = new List<object>();

            Record(observations, "initial", null, first, second, left, leftAlias,
                right, leftNeighbor, rightNeighbor);
            Record(observations, "null-cold", () => nullOwner.SendSouth(second),
                first, second, left, leftAlias, right, leftNeighbor, rightNeighbor);
            Record(observations, "left-north-cold", () => leftAlias.SendNorth(first),
                first, second, left, leftAlias, right, leftNeighbor, rightNeighbor);
            Record(observations, "right-south", () => right.SendSouth(second),
                first, second, left, leftAlias, right, leftNeighbor, rightNeighbor);
            Record(observations, "left-north-null", () => left.SendNorth(null),
                first, second, left, leftAlias, right, leftNeighbor, rightNeighbor);
            Record(observations, "right-north-first", () => right.SendNorth(first),
                first, second, left, leftAlias, right, leftNeighbor, rightNeighbor);
            Record(observations, "left-south-null", () => leftAlias.SendSouth(null),
                first, second, left, leftAlias, right, leftNeighbor, rightNeighbor);
            Record(observations, "null-warm", () => nullOwner.SendNorth(first),
                first, second, left, leftAlias, right, leftNeighbor, rightNeighbor);
            observations.Add(DeclarationFacts());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "guarded-sink" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            Action invoke, Exception first, Exception second, Forwarder left,
            Forwarder leftAlias, Forwarder right, object leftNeighbor,
            object rightNeighbor)
        {
            var initializationsBefore = SinkWitness.Initializations;
            var callsBefore = SinkWitness.Calls;
            var eventsBefore = SinkWitness.EventCount;
            var exception = "none";
            try
            {
                if (invoke != null)
                    invoke();
            }
            catch (Exception error)
            {
                exception = error.GetType().FullName;
            }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "initializationsBefore", initializationsBefore },
                { "initializationsAfter", SinkWitness.Initializations },
                { "callsBefore", callsBefore },
                { "callsAfter", SinkWitness.Calls },
                { "eventsBefore", eventsBefore },
                { "eventsAfter", SinkWitness.EventCount },
                { "initializationOrder", SinkWitness.InitializationOrder },
                { "lastCallOrder", SinkWitness.LastCallOrder },
                { "initializationsAtLastCall", SinkWitness.InitializationsAtLastCall },
                { "lastLiteral", SinkWitness.LastLiteral },
                { "lastExceptionIdentity", Identity(SinkWitness.LastException, first, second) },
                { "leftAliasSame", ReferenceEquals(left, leftAlias) },
                { "ownersDistinct", !ReferenceEquals(left, right) },
                { "leftNeighborSame", ReferenceEquals(left.Neighbor, leftNeighbor) },
                { "rightNeighborSame", ReferenceEquals(right.Neighbor, rightNeighbor) }
            });
        }

        private static string Identity(Exception value, Exception first, Exception second)
        {
            if (ReferenceEquals(value, first))
                return "first";
            if (ReferenceEquals(value, second))
                return "second";
            return value == null ? "null" : "other";
        }

        private static Dictionary<string, object> DeclarationFacts()
        {
            var owner = typeof(Forwarder);
            var north = owner.GetMethod("SendNorth",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var south = owner.GetMethod("SendSouth",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            var sink = typeof(GuardedSink);
            var accept = sink.GetMethod("Accept",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "northExact", IsWrapper(north, owner) },
                { "southExact", IsWrapper(south, owner) },
                { "wrappersDistinct", north != null && south != null && !north.Equals(south) },
                { "ownerHasNoStaticConstructor", owner.TypeInitializer == null },
                { "sinkHasExplicitStaticConstructor", sink.TypeInitializer != null },
                { "sinkExact", IsSink(accept, sink) },
                { "sinkExternal", owner.Assembly != sink.Assembly }
            };
        }

        private static bool IsWrapper(MethodInfo method, Type owner)
        {
            if (method == null || method.DeclaringType != owner ||
                method.ReturnType != typeof(void) || method.IsStatic ||
                method.IsVirtual || method.IsGenericMethod ||
                (method.GetMethodImplementationFlags() & MethodImplAttributes.NoInlining) == 0)
                return false;
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(Exception);
        }

        private static bool IsSink(MethodInfo method, Type sink)
        {
            if (method == null || method.DeclaringType != sink ||
                method.ReturnType != typeof(void) || !method.IsStatic ||
                method.IsGenericMethod)
                return false;
            var parameters = method.GetParameters();
            return parameters.Length == 2 && parameters[0].ParameterType == typeof(string) &&
                parameters[1].ParameterType == typeof(Exception);
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
