using System;
using System.Collections.Generic;
using System.Text;

namespace NTRedditBot
{
    public class Novel
    {
        public string Title { get; set; }

        public string Link { get; set; }

        public string Description { get; set; }

        public string Type { get; set; }

        public string SearchTerm { get; set; }

        public bool ExactMatch { get; set; }

        public List<string> Genres { get; set; }

        public List<string> Tags { get; set; }
    }
}
