using System.Text;

namespace ReferenceTailCallFixture
{
    public sealed class BufferOwner
    {
        public int Prefix;
        public StringBuilder Buffer;
        public int Suffix;

        public StringBuilder ClearBuffer()
        {
            return Buffer.Clear();
        }
    }
}
