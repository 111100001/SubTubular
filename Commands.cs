using System.Collections.Generic;
using CommandLine;
using YoutubeExplode;
using YoutubeExplode.Channels;
using YoutubeExplode.Playlists;

namespace SubTubular
{
    internal abstract class SearchCommand
    {
        [Option('t', "Terms", Required = true, Separator = '/',
            HelpText = "What to search for. Seperate multiple/terms/or phrases/with slashes.")]
        public IEnumerable<string> Terms { get; set; }
    }

    internal abstract class SearchPlaylistCommand : SearchCommand
    {
        [Option('l', "Latest", Default = 50, HelpText = "The number of latest videos to download and search.")]
        public int Latest { get; set; }

        [Option('h', "CachePlaylistForHours", Default = 24, HelpText = "How many hours to cache the playlist for.")]
        public float CachePlaylistForHours { get; set; }

        internal abstract string GetStorageKey();
        internal abstract IAsyncEnumerable<YoutubeExplode.Videos.Video> GetVideosAsync(YoutubeClient youtube);
    }

    [Verb("search-channel",
        HelpText = "Searches the {Latest} n videos from the Uploads playlist of the {Channel} for the specified {Terms}."
        + " This is a glorified search-playlist.")]
    internal sealed class SearchChannel : SearchPlaylistCommand
    {
        [Option('c', "Channel", Required = true, HelpText = "The channel ID or URL.")]
        public string Channel { get; set; }

        internal override string GetStorageKey() => "channel " + new ChannelId(Channel).Value;

        internal override IAsyncEnumerable<YoutubeExplode.Videos.Video> GetVideosAsync(YoutubeClient youtube)
            => youtube.Channels.GetUploadsAsync(Channel);
    }

    [Verb("search-playlist", HelpText = "Searches the {Latest} n videos from the {Playlist} for the specified {Terms}.")]
    internal sealed class SearchPlaylist : SearchPlaylistCommand
    {
        [Option('p', "Playlist", Required = true, HelpText = "The playlist ID or URL.")]
        public string Playlist { get; set; }

        internal override string GetStorageKey() => "playlist " + new PlaylistId(Playlist);

        internal override IAsyncEnumerable<YoutubeExplode.Videos.Video> GetVideosAsync(YoutubeClient youtube)
            => youtube.Playlists.GetVideosAsync(Playlist);
    }

    [Verb("search-videos", HelpText = "Searches the {Videos} for the specified {Terms}.")]
    internal sealed class SearchVideos : SearchCommand
    {
        [Option('v', "Videos", Required = true, Separator = ',', HelpText = "The comma-separated YouTube video IDs and/or URLs.")]
        public IEnumerable<string> Videos { get; set; }
    }

    [Verb("clear-cache", HelpText = "Clears cached channel, playlist and video info.")]
    internal sealed class ClearCache { }
}