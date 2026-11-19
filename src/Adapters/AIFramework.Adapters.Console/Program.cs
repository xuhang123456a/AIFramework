#nullable enable
namespace AIFramework.Adapters.Console
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            var bootstrap = new ConsoleAppBootstrap(new StandardOutputWriter());
            bootstrap.Run();
            return 0;
        }
    }
}
