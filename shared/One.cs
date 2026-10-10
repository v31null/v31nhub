using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;

namespace Hub
{
    sealed class One
    {
        readonly string name;
        Mutex mutex;

        public One(string name)
        {
            this.name = name;
        }

        public bool Take(Action<string[]> second)
        {
            if (mutex != null) return true;
            var m = new Mutex(false, @"Local\" + name);
            bool got;
            try
            {
                got = m.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                got = true;
            }
            if (!got)
            {
                m.Dispose();
                return false;
            }
            mutex = m;
            Listen(second);
            return true;
        }

        public void Tell(string[] args)
        {
            try
            {
                using (var c = new NamedPipeClientStream(".", name, PipeDirection.Out))
                {
                    c.Connect(3000);
                    var b = Text.Utf8.GetBytes(J.Stringify(args.ToList()));
                    c.Write(b, 0, b.Length);
                }
            }
            catch (Exception) { }
        }

        void Listen(Action<string[]> second)
        {
            var t = new Thread(() =>
            {
                for (;;)
                {
                    try
                    {
                        using (var s = new NamedPipeServerStream(name, PipeDirection.In, 1))
                        {
                            s.WaitForConnection();
                            var ms = new MemoryStream();
                            s.CopyTo(ms);
                            second((J.Parse(Text.Utf8.GetString(ms.ToArray())) as List<object> ?? new List<object>()).Select(J.Text).ToArray());
                        }
                    }
                    catch (Exception)
                    {
                        Thread.Sleep(200);
                    }
                }
            }) { IsBackground = true };
            t.Start();
        }
    }
}
