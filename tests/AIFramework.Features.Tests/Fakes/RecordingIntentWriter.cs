using System.Collections.Generic;
using AIFramework.Contracts.Gameplay;

namespace AIFramework.Features.Tests.Fakes
{
    /// <summary>意图写入通道录制器：只记录，不结算（结算属于内核仲裁器）。</summary>
    public sealed class RecordingIntentWriter : IIntentWriter
    {
        private readonly List<ReflectProjectileIntent> _reflectIntents;

        public RecordingIntentWriter()
        {
            _reflectIntents = new List<ReflectProjectileIntent>();
        }

        /// <summary>提交的反射意图（按提交顺序）。</summary>
        public IReadOnlyList<ReflectProjectileIntent> ReflectIntents
        {
            get { return _reflectIntents; }
        }

        /// <summary>四类意图的提交总数：用于断言"一条意图都没提交"。</summary>
        public int TotalSubmissions { get; private set; }

        /// <inheritdoc />
        public void Submit(in ControlIntent intent)
        {
            TotalSubmissions++;
        }

        /// <inheritdoc />
        public void Submit(in MoveIntent intent)
        {
            TotalSubmissions++;
        }

        /// <inheritdoc />
        public void Submit(in DamageIntent intent)
        {
            TotalSubmissions++;
        }

        /// <inheritdoc />
        public void Submit(in ReflectProjectileIntent intent)
        {
            TotalSubmissions++;
            _reflectIntents.Add(intent);
        }
    }
}
