using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FoldedStateConstructorFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object> { DeclarationFacts() };

            var firstMinimum = new FirstCell(int.MinValue);
            var secondMaximum = new SecondCell(int.MaxValue);
            var firstZero = new FirstCell(0);
            var secondNegative = new SecondCell(-17);
            var firstMaximum = new FirstCell(int.MaxValue);

            Record(observations, "first-minimum", firstMinimum, typeof(FirstCell),
                firstMinimum.State, firstMinimum.Neighbor,
                value => firstMinimum.Neighbor = value,
                () => firstMinimum.Neighbor);
            Record(observations, "second-maximum", secondMaximum, typeof(SecondCell),
                secondMaximum.State, secondMaximum.Neighbor,
                value => secondMaximum.Neighbor = value,
                () => secondMaximum.Neighbor);
            Record(observations, "first-zero", firstZero, typeof(FirstCell),
                firstZero.State, firstZero.Neighbor,
                value => firstZero.Neighbor = value,
                () => firstZero.Neighbor);
            Record(observations, "second-negative", secondNegative, typeof(SecondCell),
                secondNegative.State, secondNegative.Neighbor,
                value => secondNegative.Neighbor = value,
                () => secondNegative.Neighbor);
            Record(observations, "first-maximum", firstMaximum, typeof(FirstCell),
                firstMaximum.State, firstMaximum.Neighbor,
                value => firstMaximum.Neighbor = value,
                () => firstMaximum.Neighbor);

            var reflected = (FirstCell)typeof(FirstCell)
                .GetConstructor(new[] { typeof(int) }).Invoke(new object[] { 17 });
            Record(observations, "first-reflection", reflected, typeof(FirstCell),
                reflected.State, reflected.Neighbor,
                value => reflected.Neighbor = value,
                () => reflected.Neighbor);

            observations.Add(new Dictionary<string, object>
            {
                { "kind", "independence" },
                { "firstInstancesDistinct", !ReferenceEquals(firstMinimum, firstZero) &&
                    !ReferenceEquals(firstZero, firstMaximum) &&
                    !ReferenceEquals(firstMinimum, reflected) },
                { "secondInstancesDistinct", !ReferenceEquals(secondMaximum, secondNegative) },
                { "typesDistinct", firstMinimum.GetType() != secondMaximum.GetType() },
                { "firstMinimumRetained", firstMinimum.State == int.MinValue },
                { "firstZeroRetained", firstZero.State == 0 },
                { "firstMaximumRetained", firstMaximum.State == int.MaxValue },
                { "secondMaximumRetained", secondMaximum.State == int.MaxValue },
                { "secondNegativeRetained", secondNegative.State == -17 },
                { "reflectedRetained", reflected.State == 17 },
                { "neighborsDistinct", !ReferenceEquals(firstMinimum.Neighbor, firstZero.Neighbor) &&
                    !ReferenceEquals(firstZero.Neighbor, secondMaximum.Neighbor) }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "folded-state-constructor" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> DeclarationFacts()
        {
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "firstShape", HasShape(typeof(FirstCell)) },
                { "secondShape", HasShape(typeof(SecondCell)) },
                { "typesDistinct", typeof(FirstCell) != typeof(SecondCell) }
            };
        }

        private static bool HasShape(Type type)
        {
            var constructors = type.GetConstructors(BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            var fields = type.GetFields(BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            return type.IsSealed && type.BaseType == typeof(object) &&
                constructors.Length == 1 && constructors[0].GetParameters().Length == 1 &&
                constructors[0].GetParameters()[0].ParameterType == typeof(int) &&
                fields.Length == 2 &&
                HasField(type, "State", typeof(int)) &&
                HasField(type, "Neighbor", typeof(object));
        }

        private static bool HasField(Type type, string name, Type fieldType)
        {
            var field = type.GetField(name, BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            return field != null && field.FieldType == fieldType && !field.IsInitOnly;
        }

        private static void Record(List<object> observations, string kind, object instance,
            Type expectedType, int state, object initialNeighbor,
            Action<object> setNeighbor, Func<object> getNeighbor)
        {
            object alias = instance;
            var sentinel = new object();
            setNeighbor(sentinel);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "typeExact", instance.GetType() == expectedType },
                { "state", state },
                { "neighborInitiallyNull", initialNeighbor == null },
                { "neighborAliasSame", ReferenceEquals(getNeighbor(), sentinel) },
                { "receiverAliasSame", ReferenceEquals(alias, instance) }
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
