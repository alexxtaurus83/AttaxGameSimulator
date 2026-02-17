using System;

namespace Attax.Core {
    public interface ILogSink {
        void LogInformation(string message);
        void LogError(string message);
    }
}
