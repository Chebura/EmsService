using System;

namespace EmsService.Sending
{
    public class EmailMassSendingTaskItem
    {
        public string Receiver { get; set; }

        public bool Failed { get; set; }

        public string Failure { get; set; }

        public int? AttemptsLeft { get; set; }

        public DateTimeOffset Lifetime { get; set; }

        public DateTimeOffset? WaitFor { get; set; }

        public DateTimeOffset? Attempt { get; set; }
    }
}