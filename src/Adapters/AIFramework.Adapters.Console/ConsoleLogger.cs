using AIFramework.Contracts.Ports;
using System;

namespace AIFramework.Adapters.Console
{
    /// <summary>控制台日志端口实现。</summary>
    public sealed class ConsoleLogger : ILogger
    {
        private readonly IIOWriter _writer;

        public ConsoleLogger(IIOWriter writer)
        {
            _writer = writer;
        }

        public void Info(string message)
        {
            Write("INFO ", message);
        }

        public void Warn(string message)
        {
            Write("WARN ", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        private void Write(string level, string message)
        {
            _writer.WriteLine("[" + level + "] " + message);
        }
    }

    /// <summary>极简输出端口，便于测试时替换成内存缓冲。</summary>
    public interface IIOWriter
    {
        void WriteLine(string line);
    }

    /// <summary>把输出写到标准输出。</summary>
    public sealed class StandardOutputWriter : IIOWriter
    {
        public void WriteLine(string line)
        {
            System.Console.Out.WriteLine(line);
        }
    }
}
