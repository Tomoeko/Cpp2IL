using System.Runtime.CompilerServices;

namespace CallResultStringTailFixture
{
    public sealed class TextNode
    {
        public string Value;
        public int TextCalls;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Text()
        {
            unchecked { TextCalls++; }
            return Value;
        }
    }

    public sealed class Host
    {
        public TextNode Node;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public TextNode GetNode()
        {
            return Node;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadText()
        {
            return GetNode().Text();
        }
    }
}
