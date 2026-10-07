using System;

namespace Attax.Core {
    public interface ILogSink {
        bool IsEnabled => true;
        void LogInformation(string message);
        void LogError(string message);
    }
}
