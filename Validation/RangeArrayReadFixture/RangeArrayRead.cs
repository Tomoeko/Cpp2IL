using System.Runtime.CompilerServices;

namespace RangeArrayReadFixture
{
    public sealed class Tag
    {
    }

    public sealed class RangeArrayOwner
    {
        public int Before;
        public string[] Words;
        public Tag[] Tags;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadWord()
        {
            var words = Words;
            return words[UnityEngine.Random.Range(0, words.Length)];
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public Tag ReadTag()
        {
            var tags = Tags;
            return tags[UnityEngine.Random.Range(0, tags.Length)];
        }
    }
}
