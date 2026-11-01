using System;

namespace Attax.UserSimulator {
    public interface IConsoleIO {
        void WriteLine(string message);
        string? ReadLine();
    }

    public sealed class StandardConsoleIO : IConsoleIO {
        public void WriteLine(string message) => Console.WriteLine(message);
        public string? ReadLine() => Console.ReadLine();
    }
}
