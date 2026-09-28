using System;
using System.Collections.Generic;
using System.IO;
using RangeArrayReadFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Seeds = { 0, 1, 12345 };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var owner = new RangeArrayOwner { Before = 17 };
            var tags = new[] { new Tag(), new Tag(), new Tag() };
            foreach (var seed in Seeds)
            {
                owner.Words = new[] { "single" };
                Record(observations, "word-single", seed, owner.Words.Length,
                    owner.Words, () => owner.ReadWord());
                owner.Words = new[] { "zero", "one", "two" };
                Record(observations, "word-many", seed, owner.Words.Length,
                    owner.Words, () => owner.ReadWord());
                owner.Words = Array.Empty<string>();
                Record(observations, "word-empty", seed, owner.Words.Length,
                    owner.Words, () => owner.ReadWord());
                owner.Words = null;
                Record(observations, "word-null", seed, null,
                    null, () => owner.ReadWord());

                owner.Tags = new[] { tags[0] };
                Record(observations, "tag-single", seed, owner.Tags.Length,
                    new[] { "tag-zero" }, () => TagLabel(owner.ReadTag(), tags));
                owner.Tags = tags;
                Record(observations, "tag-many", seed, owner.Tags.Length,
                    new[] { "tag-zero", "tag-one", "tag-two" },
                    () => TagLabel(owner.ReadTag(), tags));
                owner.Tags = Array.Empty<Tag>();
                Record(observations, "tag-empty", seed, owner.Tags.Length,
                    Array.Empty<string>(), () => TagLabel(owner.ReadTag(), tags));
                owner.Tags = null;
                Record(observations, "tag-null", seed, null,
                    null, () => TagLabel(owner.ReadTag(), tags));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "range-array-read" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, int seed,
            int? length, string[] values, Func<string> invoke)
        {
            UnityEngine.Random.InitState(seed);
            int? expectedIndex = null;
            string expectedValue = null;
            string expectedException = "none";
            if (length.HasValue)
            {
                expectedIndex = UnityEngine.Random.Range(0, length.Value);
                if (expectedIndex.Value >= 0 && expectedIndex.Value < length.Value)
                    expectedValue = values[expectedIndex.Value];
                else
                    expectedException = typeof(IndexOutOfRangeException).FullName;
            }
            else
                expectedException = typeof(NullReferenceException).FullName;
            var expectedNext = UnityEngine.Random.Range(0, 100000);

            UnityEngine.Random.InitState(seed);
            string value = null;
            string exception = "none";
            try { value = invoke(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var next = UnityEngine.Random.Range(0, 100000);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "seed", seed }, { "length", length },
                { "expectedIndex", expectedIndex },
                { "expectedValue", expectedValue }, { "value", value },
                { "expectedException", expectedException }, { "exception", exception },
                { "expectedNext", expectedNext }, { "next", next }
            });
        }

        private static string TagLabel(Tag value, Tag[] tags)
        {
            for (var index = 0; index < tags.Length; index++)
                if (ReferenceEquals(value, tags[index]))
                    return new[] { "tag-zero", "tag-one", "tag-two" }[index];
            return null;
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
