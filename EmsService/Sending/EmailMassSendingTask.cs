using System.Collections.Generic;

namespace EmsService.Sending
{
    public class EmailMassSendingTask
    {
        public string TaskId { get; set; }

        public ICollection<EmailMassSendingTaskItem> Items { get; set; }
    }
}