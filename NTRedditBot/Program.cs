using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RedditNet;
using RedditNet.Auth;
using RedditNet.Requests;
using RedditNet.Things;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Dom.Html;
using AngleSharp.Parser.Html;
using Microsoft.Extensions.Configuration;
using MoreLinq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NTRedditBot.Extensions;
using RedditNet.Constants;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace NTRedditBot
{
    class Program
    {
        private static IConfigurationRoot _configuration;

        private static readonly Uri NovelUpdatesBaseUri = new Uri("http://www.novelupdates.com");

        private static readonly Random Rand = new Random();

        private static readonly HttpClient NuClient = new HttpClient();

        private static readonly HtmlParser Parser = new HtmlParser();

        static void Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(LogEventLevel.Information, theme: AnsiConsoleTheme.Code)
                .WriteTo.RollingFile("logs\\bot-{Date}.txt")
                .CreateLogger();

            var builder = new ConfigurationBuilder()
                .AddJsonFile("bot.json", optional: false);

            var config = builder.Build();

            _configuration = config;

            var cts = new CancellationTokenSource();

            var runner = new Thread(() => RunAsync(_configuration, cts.Token).GetAwaiter().GetResult())
            {
                IsBackground = true
            };

            runner.Start();

            Console.WriteLine("Running...");
            Console.ReadLine();

            Console.WriteLine("Stopping...");

            cts.Cancel();
            runner.Join();
        }

        private static async Task RunAsync(IConfigurationRoot config, CancellationToken token)
        {
            var reddit = config.GetSection("Reddit");

            var auth = new RedditPasswordAuth(reddit["ClientId"], reddit["ClientSecret"], reddit["RedirectUri"], reddit["Username"], reddit["Password"]);
            var api = new RedditApi(auth);

            var ntSubreddit = await api.GetSubredditAsync(reddit["Subreddit"], token);

            var commentReq = new GetCommentsRequest
            {
                Limit = 100,
                Sort = CommentSort.New
            };

            DateTime last = DateTime.UtcNow;
            while (true)
            {
                if (token.IsCancellationRequested)
                    break;

                try
                {
                    var comments = await ntSubreddit.GetCommentsAsync(commentReq, token);
                    commentReq.Before = comments.FirstOrDefault()?.FullName ?? commentReq.Before;

                    if (!string.IsNullOrEmpty(commentReq.Before))
                    {
                        var listing = await ntSubreddit.GetInfoAsync(new InfoRequest { Ids = commentReq.Before }, token);

                        var beforeComment = listing.OfType<Comment>().FirstOrDefault();

                        if (beforeComment == null || beforeComment.Author == "[deleted]")
                        {
                            Log.Warning("The 'before' comment has been deleted, resetting...");

                            commentReq.Before = null;

                            continue;
                        }
                    }

                    if (comments.Any())
                    {
                        last = DateTime.UtcNow;

                        Log.Debug("Processing new comments...");
                    }
                    else if (DateTime.UtcNow - last > TimeSpan.FromMinutes(15))
                    {
                        Log.Warning("No comments found in the last {Last} minutes (last before fullname: {FullName})", (int)(DateTime.UtcNow - last).TotalMinutes, commentReq.Before);

                        commentReq.Before = null;
                    }

                    foreach (Comment comment in comments.OfType<Comment>())
                    {
                        try
                        {
                            await ProcessComments(ntSubreddit, comment);
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "An error has occurred while processing a comment");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "An error has occurred while trying to retrieve comments");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            Log.Information("Cancelled.");
        }

        private static async Task ProcessComments(Subreddit subreddit, params Comment[] comments)
        {
            //Regex commentRegex = new Regex(@"\s?{{((?<novel>(.?[^}}])+)}})\s?", RegexOptions.Compiled);
            Regex commentRegex = new Regex(@"\s?\[\[((?<novel>(.?[^\]\]])+))\]\]\s?", RegexOptions.Compiled);

            string selfUser = _configuration["Reddit:Username"];

            foreach (Comment comment in comments)
            {
                var listing = await subreddit.GetCommentAndRepliesAsync(new GetCommentRepliesRequest
                {
                    CommentId = comment.Id,
                    LinkId = comment.LinkId
                });

                var commentWithReplies = listing.OfType<Comment>().FirstOrDefault();

                if (commentWithReplies?.Replies.OfType<Comment>().Any(p => p.Author == selfUser) ?? false)
                {
                    Log.Warning("Already replied to comment by {Author}, skipping...", comment.Author);

                    continue;
                }

                var commentMatches = commentRegex.Matches(comment.Body).Cast<Match>().ToList();
                var novels = new List<Novel>();

                if (commentMatches.Any())
                {
                    Log.Information("Processing comment by {Commenter} in {Link}...", comment.Author, comment.LinkTitle);
                }

                foreach (Match commentMatch in commentMatches)
                {
                    string novelTitle = commentMatch.Groups["novel"].Value.Trim();

                    Log.Debug("Got match: {Match} from {From}", novelTitle, commentMatch.Value.Trim());

                    var novel = await SearchNovelUpdates(novelTitle);

                    if (novel != null)
                    {
                        Log.Debug("Found novel: {Novel}", novel.Title);

                        novel.SearchTerm = novelTitle;
                        novels.Add(novel);
                    }
                }

                await SubmitReplyTo(comment, novels.DistinctBy(n => n.Title).ToList());
            }
        }

        private static async Task SubmitReplyTo(Comment comment, List<Novel> novels)
        {
            if (!novels.Any())
                return;

            StringBuilder sb = new StringBuilder();

            int ctr = 0;
            foreach (Novel novel in novels.Take(6))
            {
                var tagList = novel.Tags.OrderBy(_ => Rand.Next()).Take(6);
                var genreList = novel.Genres.Take(6);

                string genres = string.Join(", ", genreList);
                string tags = string.Join(", ", tagList);

                string desc = novel.Description.Truncate(256);

                if (!novel.ExactMatch)
                {
                    sb.AppendLine("Closest match for:");
                    sb.AppendLine();
                    sb.AppendLine($"> {novel.SearchTerm}");
                    sb.AppendLine();
                }

                sb.AppendLine($"**{novel.Title}** - ([Novel Updates]({novel.Link}))");
                sb.AppendLine();
                sb.AppendLine($"Description: {desc}");
                sb.AppendLine();
                sb.AppendLine($"{novel.Type} | Genres: {genres} | Tags: {tags}");

                if (ctr < novels.Count - 1)
                {
                    sb.AppendLine();
                }

                ctr++;
            }

            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine("^[Source](https://github.com/Mitch528/F5SectMemberBot)");

            string body = sb.ToString();

            var resp = await comment.Api.PostAsync(UrlConstants.SubmitCommentUrl, new CommentRequest
            {
                Text = body,
                ThingId = comment.FullName
            });

            string json = await resp.Content.ReadAsStringAsync();
            JToken obj = JObject.Parse(json);

            if (obj["json"] != null)
                obj = obj["json"];

            Log.Debug("Got response {Response}", json);

            if (obj["ratelimit"] != null)
            {
                double waitFor = obj["ratelimit"].Value<double>();

                TimeSpan waitForTs = TimeSpan.FromSeconds(waitFor);

                Log.Warning("Reached the rate limit! Will retry in {WaitMin} minutes ({WaitSec} seconds)", (int)waitForTs.TotalMinutes,
                    waitFor);

                await Task.Delay(waitForTs + TimeSpan.FromSeconds(1));

                await comment.ReplyAsync(body);
            }

            Log.Information("Sent reply to {Author} containing novels {Novels}", comment.Author, novels.Select(p => p.Title));

            resp.Dispose();
        }

        private static async Task<Novel> SearchNovelUpdates(string keyword)
        {
            string html = await NuClient.GetStringAsync($"{NovelUpdatesBaseUri.AbsoluteUri}/?s={keyword}");

            IHtmlDocument doc = await Parser.ParseAsync(html);

            var novelList = doc.QuerySelectorAll(".w-blog-list a.w-blog-entry-link")
                .ToList();

            var dict = new Dictionary<string, string>();

            foreach (IElement nSearchEl in novelList)
            {
                string title = nSearchEl.QuerySelector(".entry-title").TextContent.Trim();
                string link = nSearchEl.GetAttribute("href");

                var novel = await ProcessNovel(keyword, link, false);

                if (novel != null)
                {
                    return novel;
                }

                dict.Add(title, link);
            }

            // If we find none that matches, grab the closest matching novel

            var closest = dict
                .FirstOrDefault(p => p.Key.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);

            if (closest.Key != null)
            {
                Log.Information("Found no exact matches for keyword {Keyword}, using closest novel found: {Closest}", keyword, closest.Key);

                return await ProcessNovel(closest.Key, closest.Value, true);
            }

            return null;
        }

        private static async Task<Novel> ProcessNovel(string keyword, string url, bool force)
        {
            string html = await NuClient.GetStringAsync(url);

            IHtmlDocument doc = await Parser.ParseAsync(html);

            string title = doc.QuerySelector(".seriestitlenu").TextContent
                .Replace('\u2018', '\'').Replace('\u2019', '\'').Trim();

            var assoc = doc.GetElementById("editassociated");

            var assocList = assoc.QuerySelectorAll("br").Select(p => p.PreviousSibling).Where(p => p?.NodeType == NodeType.Text)
                .Concat(assoc)
                .ToList();

            if (!force && !keyword.Equals(title, StringComparison.OrdinalIgnoreCase)
                && !assocList.Any(p => p.TextContent.Trim().Equals(keyword, StringComparison.OrdinalIgnoreCase)))
            {
                Log.Debug("Could not find novel {Novel} from list {NovelList}", keyword, assocList.Select(p => p.TextContent));

                return null;
            }

            var descEl = doc.GetElementById("editdescription");
            string desc = descEl.TextContent;

            string type = doc.GetElementById("showtype").TextContent;

            var genreList = doc.GetElementById("seriesgenre");

            var genres = genreList.QuerySelectorAll("a.genre")
                .Select(p => p.TextContent)
                .ToList();

            var tagsList = doc.GetElementById("showtags");

            var tags = tagsList.QuerySelectorAll("a.genre")
                .Select(p => p.TextContent)
                .ToList();

            var novel = new Novel
            {
                Title = title,
                Description = desc,
                Type = type,
                Link = url,
                Genres = genres,
                Tags = tags,
                ExactMatch = !force
            };

            return novel;
        }
    }
}