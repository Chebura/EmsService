using System;

namespace EmsService.Configuration
{
    public class RollUpStrategyConfiguration
    {
        public bool Enable { get; set; }

        public TimeSpan Delay { get; set; }

        public int MaxCount { get; set; }
    }
}