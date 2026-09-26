using System.Runtime.CompilerServices;

namespace SharedAbiFixture
{
    public sealed class FloatSink
    {
        public float A;
        public long GapA;
        public float B;
        public long GapB;
        public float C;
        public long GapC;
        public float D;
        public long GapD;
        public float E;
        public long GapE;
        public float F;
        public long GapF;
        public float G;
        public long GapG;
        public float H;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Accept(float value)
        {
            A = value;
            B = value;
            C = value;
            D = value;
            E = value;
            F = value;
            G = value;
            H = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float Forward(float value) { Accept(value); return H; }
    }

    public sealed class FloatTwin
    {
        public float A;
        public long GapA;
        public float B;
        public long GapB;
        public float C;
        public long GapC;
        public float D;
        public long GapD;
        public float E;
        public long GapE;
        public float F;
        public long GapF;
        public float G;
        public long GapG;
        public float H;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Accept(float value)
        {
            A = value;
            B = value;
            C = value;
            D = value;
            E = value;
            F = value;
            G = value;
            H = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public float Forward(float value) { Accept(value); return H; }
    }

    public sealed class IntegerSink
    {
        public int A;
        public int B;
        public int C;
        public int D;
        public int E;
        public int F;
        public int G;
        public int H;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Accept(int value)
        {
            A = value;
            B = value;
            C = value;
            D = value;
            E = value;
            F = value;
            G = value;
            H = value;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Forward(int value) { Accept(value); return H; }
    }

    public sealed class StackSlots
    {
        public int A;
        public int B;
        public int C;
        public int D;
        public int E;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Write(int a, int b, int c, int d, int e)
        {
            A = a;
            B = b;
            C = c;
            D = d;
            E = e;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public int Forward(int a, int b, int c, int d, int e)
        {
            Write(a, b, c, d, e);
            return E;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void TailForward(int a, int b, int c, int d, int e)
        {
            Write(a, b, c, d, e);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ReadSixth(int a, int b, int c, int d, int e, int f) { return f; }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int ForwardSixth(int a, int b, int c, int d, int e, int f)
        {
            return ReadSixth(a, b, c, d, e, f);
        }
    }
}
