using System;

using Topshelf;

namespace Hansoft.Jean
{
    class Jean
    {
        public static void Main()
        {
            HostFactory.Run(x =>
            {
                x.Service<JeanService>(s =>
                {
                    s.ConstructUsing(name => new JeanService());
                    s.WhenStarted(jean => jean.Start());
                    s.WhenStopped(jean => jean.Stop());
                });
                x.DependsOnEventLog();
                x.RunAsLocalSystem();

                x.SetDescription("Automatic updating of Hansoft Items");
                x.SetDisplayName("Jean for Hansoft");
                x.SetServiceName("Jean");
            });
        }
    }
}
