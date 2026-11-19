using System;
using System.Collections.Generic;

namespace EmsService.Configuration
{
    public class GroupConfiguration
    {
        public string PlainTextFileName { get; set; }

        public string Encoding { get; set; }

        public TimeSpan? Actuality { get; set; }

        public ICollection<string> Receivers { get; set; }

        public string Subject { get; set; }
    }

}