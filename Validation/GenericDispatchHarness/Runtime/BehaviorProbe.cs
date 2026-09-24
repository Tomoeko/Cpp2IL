using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GenericDispatchFixture;
using Neutral.GenericDispatch;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object> { DeclarationFacts() };
            var left = new Forwarder();
            var right = new Forwarder();
            var leftNeighbor = new object();
            var rightNeighbor = new object();
            left.Neighbor = leftNeighbor;
            right.Neighbor = rightNeighbor;

            IRead<Payload> leftPayload = left;
            IRead<string> leftText = left;
            GenericDispatchBase leftBase = left;
            IRead<Payload> rightPayload = right;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "initial" },
                { "leftCalls", left.Calls }, { "leftLastKey", left.LastKey },
                { "rightCalls", right.Calls }, { "rightLastKey", right.LastKey },
                { "leftAliasesSame", ReferenceEquals(leftPayload, leftText) &&
                    ReferenceEquals(leftText, leftBase) },
                { "ownersDistinct", !ReferenceEquals(leftPayload, rightPayload) },
                { "leftNeighborSame", ReferenceEquals(left.Neighbor, leftNeighbor) },
                { "rightNeighborSame", ReferenceEquals(right.Neighbor, rightNeighbor) }
            });

            Record(observations, "left-payload", left, right, leftNeighbor, rightNeighbor,
                () => leftPayload.Read());
            Record(observations, "right-payload", right, left, rightNeighbor, leftNeighbor,
                () => rightPayload.Read());
            Record(observations, "left-text", left, right, leftNeighbor, rightNeighbor,
                () => leftText.Read());
            Record(observations, "left-payload-repeat", left, right, leftNeighbor, rightNeighbor,
                () => leftPayload.Read());
            Record(observations, "left-nongeneric", left, right, leftNeighbor, rightNeighbor,
                () => leftBase.InvokeNongeneric(31));

            IRead<Payload> nullPayload = null;
            IRead<string> nullText = null;
            Record(observations, "null-payload", left, right, leftNeighbor, rightNeighbor,
                () => nullPayload.Read());
            Record(observations, "null-text", left, right, leftNeighbor, rightNeighbor,
                () => nullText.Read());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "generic-dispatch" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> DeclarationFacts()
        {
            var owner = typeof(Forwarder);
            var payloadInterface = typeof(IRead<Payload>);
            var textInterface = typeof(IRead<string>);
            var payloadMap = owner.GetInterfaceMap(payloadInterface);
            var textMap = owner.GetInterfaceMap(textInterface);
            var payloadTarget = payloadMap.TargetMethods.Length == 1
                ? payloadMap.TargetMethods[0] : null;
            var textTarget = textMap.TargetMethods.Length == 1
                ? textMap.TargetMethods[0] : null;
            MethodInfo genericBase = null;
            MethodInfo nongenericBase = null;
            foreach (var method in typeof(GenericDispatchBase).GetMethods(
                         BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.Name != "Choose")
                    continue;
                if (method.IsGenericMethodDefinition)
                    genericBase = method;
                else
                    nongenericBase = method;
            }

            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "payloadReturnExact", payloadInterface.GetMethod("Read").ReturnType == typeof(Payload) },
                { "textReturnExact", textInterface.GetMethod("Read").ReturnType == typeof(string) },
                { "payloadExplicit", IsExplicitImplementation(payloadTarget, owner, typeof(Payload)) },
                { "textExplicit", IsExplicitImplementation(textTarget, owner, typeof(string)) },
                { "targetsDistinct", payloadTarget != null && textTarget != null &&
                    !payloadTarget.Equals(textTarget) },
                { "genericBaseDefinition", IsGenericBase(genericBase) },
                { "nongenericBaseOverload", IsNongenericBase(nongenericBase) }
            };
        }

        private static bool IsExplicitImplementation(MethodInfo method, Type owner, Type returnType)
        {
            return method != null && method.DeclaringType == owner &&
                method.ReturnType == returnType && method.GetParameters().Length == 0 &&
                method.IsPrivate && method.IsVirtual && method.IsFinal &&
                (method.Attributes & MethodAttributes.VtableLayoutMask) == MethodAttributes.NewSlot;
        }

        private static bool IsGenericBase(MethodInfo method)
        {
            if (method == null || !method.IsFamily || method.GetGenericArguments().Length != 1 ||
                !method.ReturnType.IsGenericParameter)
                return false;
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
        }

        private static bool IsNongenericBase(MethodInfo method)
        {
            if (method == null || !method.IsFamily || method.IsGenericMethod ||
                method.ReturnType != typeof(object))
                return false;
            var parameters = method.GetParameters();
            return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
        }

        private static void Record(List<object> observations, string kind, Forwarder active,
            Forwarder other, object activeNeighbor, object otherNeighbor, Func<object> invoke)
        {
            var activeBefore = active.Calls;
            var activeKeyBefore = active.LastKey;
            var otherBefore = other.Calls;
            var otherKeyBefore = other.LastKey;
            object result = null;
            var exception = "none";
            try { result = invoke(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception },
                { "resultIsNull", result == null },
                { "resultType", result == null ? null : result.GetType().FullName },
                { "activeCallsBefore", activeBefore }, { "activeCallsAfter", active.Calls },
                { "activeKeyBefore", activeKeyBefore }, { "activeKeyAfter", active.LastKey },
                { "otherCallsBefore", otherBefore }, { "otherCallsAfter", other.Calls },
                { "otherKeyBefore", otherKeyBefore }, { "otherKeyAfter", other.LastKey },
                { "activeNeighborSame", ReferenceEquals(active.Neighbor, activeNeighbor) },
                { "otherNeighborSame", ReferenceEquals(other.Neighbor, otherNeighbor) }
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
