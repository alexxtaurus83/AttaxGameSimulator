using System;
using Attax.Core;

namespace Attax.Console {
    internal sealed class ConsoleLogSink : ILogSink {
        private readonly string _prefix;

        public ConsoleLogSink(string prefix) {
            _prefix = prefix;
        }

        public void LogInformation(string message) =>
            System.Console.WriteLine($"[{_prefix}] {message}");

        public void LogError(string message) =>
            System.Console.WriteLine($"[{_prefix}][ERROR] {message}");
    }
}
