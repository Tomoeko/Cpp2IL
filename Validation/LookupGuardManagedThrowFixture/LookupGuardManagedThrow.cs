using System;
using System.Runtime.CompilerServices;
using Neutral.LookupGuard;

namespace LookupGuardManagedThrowFixture
{
    public class RecordReader
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public string Read(int key)
        {
            var record = RecordSource.GetService().Find(key);
            if (record == null)
                throw new ArgumentException("Missing key: " + key.ToString());

            return record.Text;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadProperty(int key)
        {
            var record = RecordSource.GetService().Find(key);
            if (record == null)
                throw new ArgumentException("Missing property key: " + key.ToString());

            return record.PropertyText;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public string ReadInherited(int key)
        {
            var record = RecordSource.GetService().Find(key);
            if (record == null)
                throw new ArgumentException("Missing inherited key: " + key.ToString());

            return ((BaseTextRecord)record).InheritedText;
        }
    }
}
