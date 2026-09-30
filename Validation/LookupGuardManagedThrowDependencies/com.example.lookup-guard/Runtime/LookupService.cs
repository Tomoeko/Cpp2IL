using System;
using System.Runtime.CompilerServices;

namespace Neutral.LookupGuard
{
    public static class LookupEffects
    {
        public static int Initializations;
        public static int ProducerCalls;
        public static int LookupCalls;
        public static int LastKey;

        public static void Reset()
        {
            Initializations = 0;
            ProducerCalls = 0;
            LookupCalls = 0;
            LastKey = 0;
        }
    }

    public class TextRecord
    {
        public string Text;
        private string _propertyText;

        public string PropertyText
        {
            get { return _propertyText; }
            set { _propertyText = value; }
        }
    }

    public class LookupService
    {
        public TextRecord Current;
        public bool ThrowOnLookup;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public TextRecord Find(int key)
        {
            LookupEffects.LookupCalls++;
            LookupEffects.LastKey = key;
            if (ThrowOnLookup)
                throw new InvalidOperationException("lookup failure");
            return Current;
        }
    }

    public static class RecordSource
    {
        public static LookupService Current;

        static RecordSource()
        {
            LookupEffects.Initializations++;
            Current = new LookupService { Current = new TextRecord { Text = "first" } };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static LookupService GetService()
        {
            LookupEffects.ProducerCalls++;
            return Current;
        }
    }
}
