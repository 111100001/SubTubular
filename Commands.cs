using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using CommandLine;
using YoutubeExplode;
using YoutubeExplode.Channels;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos;

namespace SubTubular
{
    internal abstract class SearchCommand
    {
        /// <summary>Enables having a multi-word <see cref="Query"/> (i.e. with spaces in between parts)
        /// without having to quote it and double-quote multi-word expressions within it.</summary>
        [Option('f', "for", Required = true, HelpText = "What to search for."
            + " Quote \"multi-word phrases\" and separate multiple|terms by pipe."
            + " Learn more about the query syntax at https://mikegoatly.github.io/lifti/docs/searching/lifti-query-syntax/ .")]
        public IEnumerable<string> QueryWords { set { Query = value.Join(" "); } }

        public string Query { get; private set; }

        [Option('p', "pad", Default = (ushort)23, HelpText = "How much context to display a match in;"
            + " i.e. the minimum number of characters of the original text to display before and after it.")]
        public ushort Padding { get; set; }

        [Option('m', "html",
            HelpText = "If set, outputs the highlighted search result in an HTML file including hyperlinks for easy navigation."
                + " The output path depends on the 'out' parameter.")]
        public bool OutputHtml { get; set; }

        [Option('o', "out",
            HelpText = "Writes the search results to a file, the format of which - depending on the 'html' flag -"
                + " is either text or HTML including hyperlinks for easy navigation."
                + " Supply EITHER the FULL FILE PATH (any existing file will be overwritten),"
                + " a FOLDER PATH to output files into - auto-named according to your search parameters -"
                + " OR OMIT while setting the 'html' flag to have auto-named files written"
                + " to the 'out' folder of SubTubular's AppData directory.")]
        public string FileOutputPath { get; set; }

        [Option('s', "show", HelpText = "The output to open if a file was written.")]
        public Shows? Show { get; set; }

        internal abstract string Label { get; }
        internal abstract IEnumerable<string> GetUrls();
        protected abstract string FormatInternal();
        internal string Format() => FormatInternal() + " " + Query;

        public enum Shows { file, folder }

        internal virtual void Validate()
        {
            if (string.IsNullOrWhiteSpace(Query)) throw new InputException(
                "None of the terms contain anything but whitespace. I refuse to work like this!");
        }
    }

    internal abstract class SearchPlaylistCommand : SearchCommand
    {
        [Option('t', "top", Default = (ushort)50,
            HelpText = "The number of videos to return from the top of the playlist."
                + " The special Uploads playlist of a channel or user are sorted latest uploaded first,"
                + " but custom playlists may be sorted differently.")]
        public ushort Top { get; set; }

        [Option('r', "order-by", HelpText = "Order the output by 'uploaded' or 'score' with 'desc' for descending.")]
        public IEnumerable<OrderOptions> OrderBy { get; set; }

        [Option('h', "cache-hours", Default = 24, HelpText = "The maximum age of a playlist cache in hours"
            + " before it is considered stale and the videos in it are refreshed.")]
        public float CacheHours { get; set; }

        protected abstract string ID { get; }
        protected abstract string UrlFormat { get; }
        internal override IEnumerable<string> GetUrls() { yield return UrlFormat + ID; }
        internal string StorageKey => Label + ID;
        protected override string FormatInternal() => StorageKey;
        internal abstract IAsyncEnumerable<PlaylistVideo> GetVideosAsync(YoutubeClient youtube, CancellationToken cancellation);

        internal override void Validate()
        {
            base.Validate();

            if (OrderBy.Intersect(Orders).Count() > 1) throw new InputException(
                $"You may order by either '{nameof(OrderOptions.score)}' or '{nameof(OrderOptions.uploaded)}' (date), but not both.");

            // default to ordering by highest score which is probably most useful for most purposes
            if (!OrderBy.Any()) OrderBy = new[] { OrderOptions.score };
        }

        /// <summary>Mutually exclusive <see cref="OrderOptions"/>.</summary>
        internal static OrderOptions[] Orders = new[] { OrderOptions.uploaded, OrderOptions.score };

        /// <summary><see cref="Orders"/> and modifiers.</summary>
        public enum OrderOptions { uploaded, score, asc }
    }

    [Verb("search-user", aliases: new[] { "user", "u" },
        HelpText = "Searches the videos in the Uploads playlist of a user's main channel."
            + " This is a glorified search-playlist.")]
    internal sealed class SearchUser : SearchPlaylistCommand
    {
        internal const string StorageKeyPrefix = "user ";

        [Value(0, MetaName = "user", Required = true, HelpText = "The user name or URL.")]
        public string User { get; set; }

        internal override string Label => StorageKeyPrefix;
        protected override string ID => UserName.Parse(User).Value;
        protected override string UrlFormat => "https://www.youtube.com/user/";

        internal override async IAsyncEnumerable<PlaylistVideo> GetVideosAsync(YoutubeClient youtube, [EnumeratorCancellation] CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var channel = await youtube.Channels.GetByUserAsync(User, cancellation);

            await foreach (var video in youtube.Channels.GetUploadsAsync(channel.Id, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                yield return video;
            }
        }
    }

    [Verb("search-channel", aliases: new[] { "channel", "c" },
        HelpText = "Searches the videos in a channel's Uploads playlist."
        + " This is a glorified search-playlist.")]
    internal sealed class SearchChannel : SearchPlaylistCommand
    {
        internal const string StorageKeyPrefix = "channel ";

        [Value(0, MetaName = "channel", Required = true, HelpText = "The channel ID or URL.")]
        public string Channel { get; set; }

        internal override string Label => StorageKeyPrefix;
        protected override string ID => ChannelId.Parse(Channel).Value;
        protected override string UrlFormat => "https://www.youtube.com/channel/";

        internal override IAsyncEnumerable<PlaylistVideo> GetVideosAsync(YoutubeClient youtube, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return youtube.Channels.GetUploadsAsync(Channel, cancellation);
        }
    }

    [Verb("search-playlist", aliases: new[] { "playlist", "p" }, HelpText = "Searches the videos in a playlist.")]
    internal sealed class SearchPlaylist : SearchPlaylistCommand
    {
        internal const string StorageKeyPrefix = "playlist ";

        [Value(0, MetaName = "playlist", Required = true, HelpText = "The playlist ID or URL.")]
        public string Playlist { get; set; }

        internal override string Label => StorageKeyPrefix;
        protected override string ID => PlaylistId.Parse(Playlist).Value;
        protected override string UrlFormat => "https://www.youtube.com/playlist?list=";

        internal override IAsyncEnumerable<PlaylistVideo> GetVideosAsync(YoutubeClient youtube, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            return youtube.Playlists.GetVideosAsync(Playlist, cancellation);
        }
    }

    [Verb("search-videos", aliases: new[] { "videos", "v" }, HelpText = "Searches the specified videos.")]
    internal sealed class SearchVideos : SearchCommand
    {
        internal static string GetVideoUrl(string videoId) => "https://youtu.be/" + videoId;

        [Value(0, MetaName = "videos", Required = true, HelpText = "The space-separated YouTube video IDs and/or URLs.")]
        public IEnumerable<string> Videos { get; set; }

        internal override string Label => "videos ";
        internal IEnumerable<string> GetVideoIds() => Videos.Select(v => VideoId.Parse(v).Value);
        protected override string FormatInternal() => Label + GetVideoIds().Join(" ");
        internal override IEnumerable<string> GetUrls() => GetVideoIds().Select(id => GetVideoUrl(id));
    }

    [Verb("open", aliases: new[] { "o" }, HelpText = "Opens app-related folders in a file browser.")]
    internal sealed class Open
    {
        [Value(0, MetaName = "folder", Required = true, HelpText = "The folder to open.")]
        public Folders Folder { get; set; }
    }

    [Serializable]
    internal class InputException : Exception
    {
        public InputException(string message) : base(message) { }
        public InputException(string message, Exception innerException) : base(message, innerException) { }
    }
}