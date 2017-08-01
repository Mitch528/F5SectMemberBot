using System;
using System.Collections.Generic;
using System.Text;

namespace NTRedditBot.Extensions
{
    public static class StringExtensions
    {
        public static string Truncate(this string str, int length)
        {
            if (string.IsNullOrEmpty(str))
                return string.Empty;

            string[] words = str.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            if (words[0].Length > length)
                return words[0].Substring(0, length);

            var sb = new StringBuilder();

            foreach (string word in words)
            {
                if ((sb + word).Length > length)
                    return $"{sb.ToString().TrimEnd(' ')}...";

                sb.Append(word + " ");
            }

            return sb.ToString().TrimEnd(' ');
        }
    }
}
