using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FoldedLiteralConstructorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var first = new FirstCell();
            var second = new SecondCell();
            var third = new ThirdCell();
            var firstAgain = new FirstCell();
            var reflected = (SecondCell)typeof(SecondCell).GetConstructor(Type.EmptyTypes)
                .Invoke(new object[0]);

            var observations = new List<object>
            {
                DeclarationFacts(),
                Observe("first", first.GetType(), typeof(FirstCell), first.State,
                    first.Neighbor, first.Guard),
                Observe("second", second.GetType(), typeof(SecondCell), second.State,
                    second.Neighbor, second.Guard),
                Observe("third", third.GetType(), typeof(ThirdCell), third.State,
                    third.Neighbor, third.Guard),
                Observe("first-again", firstAgain.GetType(), typeof(FirstCell),
                    firstAgain.State, firstAgain.Neighbor, firstAgain.Guard),
                Observe("second-reflection", reflected.GetType(), typeof(SecondCell),
                    reflected.State, reflected.Neighbor, reflected.Guard)
            };

            var sentinel = new object();
            first.Neighbor = sentinel;
            first.Guard = 71;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "independence" },
                { "firstAndRepeatedDistinct", !ReferenceEquals(first, firstAgain) },
                { "secondAndReflectedDistinct", !ReferenceEquals(second, reflected) },
                { "typesDistinct", first.GetType() != second.GetType() &&
                    first.GetType() != third.GetType() && second.GetType() != third.GetType() },
                { "firstNeighborAlias", ReferenceEquals(first.Neighbor, sentinel) },
                { "firstGuard", first.Guard },
                { "firstState", first.State },
                { "repeatedNeighborNull", firstAgain.Neighbor == null },
                { "repeatedGuard", firstAgain.Guard },
                { "repeatedState", firstAgain.State },
                { "secondState", second.State },
                { "thirdState", third.State },
                { "reflectedState", reflected.State }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "folded-literal-constructor" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> DeclarationFacts()
        {
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "firstShape", HasShape(typeof(FirstCell), false) },
                { "secondShape", HasShape(typeof(SecondCell), true) },
                { "thirdShape", HasShape(typeof(ThirdCell), true) }
            };
        }

        private static bool HasShape(Type type, bool sealedType)
        {
            var constructors = type.GetConstructors(BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            var fields = type.GetFields(BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            return type.IsSealed == sealedType && type.BaseType == typeof(object) &&
                constructors.Length == 1 && constructors[0].GetParameters().Length == 0 &&
                fields.Length == 3 && HasField(type, "Neighbor", typeof(object)) &&
                HasField(type, "State", typeof(int)) && HasField(type, "Guard", typeof(int));
        }

        private static bool HasField(Type type, string name, Type fieldType)
        {
            var field = type.GetField(name, BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            return field != null && field.FieldType == fieldType && !field.IsInitOnly;
        }

        private static Dictionary<string, object> Observe(string kind, Type actualType,
            Type expectedType, int state, object neighbor, int guard)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "typeExact", actualType == expectedType },
                { "state", state },
                { "neighborInitiallyNull", neighbor == null },
                { "guard", guard }
            };
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
