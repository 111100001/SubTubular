﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;

namespace SubTubular
{
    internal sealed class Program
    {
        private const string asciiHeading = @"
   _____       __  ______      __          __
  / ___/__  __/ /_/_  __/_  __/ /_  __  __/ /___ ______
  \__ \/ / / / __ \/ / / / / / __ \/ / / / / __ `/ ___/
 ___/ / /_/ / /_/ / / / /_/ / /_/ / /_/ / / /_/ / /
/____/\__,_/_.___/_/  \__,_/_.___/\__,_/_/\__,_/_/

"; //from http://www.patorjk.com/software/taag/#p=display&f=Slant&t=SubTubular

        private const string repoUrl = "https://github.com/h0lg/SubTubular";
        private static string errorOutputSpacing = Environment.NewLine + Environment.NewLine;

        private static async Task Main(string[] args)
        {
#if DEBUG
            Tests.PaddedMatchTests.Run();
#endif

            var originalCommand = "> SubTubular " + args.Join(" ");

            //see https://github.com/commandlineparser/commandline
            try
            {
                var parserResult = new Parser(with => with.HelpWriter = null)
                    .ParseArguments<SearchUser, SearchChannel, SearchPlaylist, SearchVideos, Open, ClearCache>(args);

                //https://github.com/commandlineparser/commandline/wiki/Getting-Started#using-withparsedasync-in-asyncawait
                await parserResult.WithParsedAsync<SearchUser>(command
                    => SearchAsync(command, originalCommand, youtube => youtube.SearchPlaylistAsync(command)));

                await parserResult.WithParsedAsync<SearchChannel>(command
                    => SearchAsync(command, originalCommand, youtube => youtube.SearchPlaylistAsync(command)));

                await parserResult.WithParsedAsync<SearchPlaylist>(command
                    => SearchAsync(command, originalCommand, youtube => youtube.SearchPlaylistAsync(command)));

                await parserResult.WithParsedAsync<SearchVideos>(command
                    => SearchAsync(command, originalCommand, youtube => youtube.SearchVideosAsync(command)));

                await parserResult.WithParsedAsync<ClearCache>(command => command.Process());
                parserResult.WithParsed<Open>(open => ShellCommands.ExploreFolder(Folder.GetPath(open.Folder)));

                //see https://github.com/commandlineparser/commandline/wiki/HelpText-Configuration
                parserResult.WithNotParsed(errors => Console.WriteLine(HelpText.AutoBuild(parserResult, h =>
                {
                    if (parserResult.Errors.Any(error => error.Tag == ErrorType.NoVerbSelectedError))
                        h.Heading = asciiHeading + h.Heading; // enhance heading for branding
                    else // remove heading and copyright to reduce noise before error if a verb is already selected
                    {
                        h.Heading = string.Empty;
                        h.Copyright = string.Empty;
                    }

                    h.AddEnumValuesToHelpText = true;
                    h.OptionComparison = CompareOptions;
                    h.AddPostOptionsLine($"See {repoUrl} for more info.");
                    return h;
                })));
            }
            catch (Exception ex) { await WriteErrorLogAsync(originalCommand, ex.ToString()); }
        }

        private static async Task SearchAsync(SearchCommand command, string originalCommand,
            Func<Youtube, IAsyncEnumerable<VideoSearchResult>> getResultsAsync)
        {
            if (string.IsNullOrWhiteSpace(command.Query))
            {
                Console.WriteLine("None of the terms contain anything but whitespace. I refuse to work like this!");
                return;
            }

            if (command is SearchPlaylistCommand searchPlaylist) // order playlist matches
            {
                if (searchPlaylist.OrderBy.Intersect(SearchPlaylistCommand.Orders).Count() > 1)
                {
                    Console.WriteLine("You may order by either 'score' or 'uploaded' (date), but not both.");
                    return;
                }

                // default to ordering by highest score which is probably most useful for most purposes
                if (!searchPlaylist.OrderBy.Any()) searchPlaylist.OrderBy = new[] { SearchPlaylistCommand.OrderOptions.score };
            }

            //inspired by https://johnthiriet.com/cancel-asynchronous-operation-in-csharp/
            using (var search = new CancellationTokenSource())
            {
                var searching = true;

                var cancel = Task.Run(async () => //start in background, don't wait for completion
                {
                    Console.WriteLine("Press any key to cancel");
                    Console.WriteLine();

                    /* wait for key or search to finish, non-blockingly; but only as long as to not cause perceivable lag
                        inspired by https://stackoverflow.com/a/5620647 and https://stackoverflow.com/a/23628232 */
                    while (searching && !Console.KeyAvailable) await Task.Delay(200, search.Token);

                    if (Console.KeyAvailable) Console.ReadKey(true); //consume cancel key without displaying it
                    if (searching) search.Cancel();
                });

                var cacheFolder = Folder.GetPath(Folders.cache);
                var youtube = new Youtube(new JsonFileDataStore(cacheFolder), new VideoIndexRepository(cacheFolder));
                var tracksWithErrors = new List<CaptionTrack>();

                using (var output = new OutputWriter(originalCommand, command))
                {
                    try
                    {
                        /* passing token into search implementations for them to react to cancellation,
                            see https://docs.microsoft.com/en-us/archive/msdn-magazine/2019/november/csharp-iterating-with-async-enumerables-in-csharp-8#a-tour-through-async-enumerables*/
                        await foreach (var result in getResultsAsync(youtube).WithCancellation(search.Token))
                        {
                            output.DisplayVideoResult(result);
                            tracksWithErrors.AddRange(result.Video.CaptionTracks.Where(t => t.Error != null));
                        }
                    }
                    catch (OperationCanceledException) { Console.WriteLine("The search was cancelled."); }

                    //only writes an output file if command requires it
                    var path = await output.WriteOutputFile(() => Folder.GetPath(Folders.output));

                    if (path != null)
                    {
                        Console.WriteLine("Search results were written to " + path);

                        // spare the user some file browsing
                        if (command.Show == SearchCommand.Shows.file) ShellCommands.OpenFile(path);
                        if (command.Show == SearchCommand.Shows.folder) ShellCommands.ExploreFolder(path);
                    }
                }

                if (tracksWithErrors.Count > 0)
                {
                    await WriteErrorLogAsync(originalCommand, tracksWithErrors.Select(t =>
@$"{t.LanguageName}: {t.ErrorMessage}

  {t.Url}

  {t.Error}").Join(errorOutputSpacing), command.Format());
                }

                searching = false; // to let the cancel task complete if search did before it
                await cancel; // just to rethrow possible exceptions
            }
        }

        private static async Task WriteErrorLogAsync(string originalCommand, string errors, string name = null)
        {
            try
            {
                var fileSafeName = name == null ? null : " " + name.ToFileSafe();
                var path = Path.Combine(Folder.GetPath(Folders.errors), $"error {DateTime.Now:yyyy-MM-dd HHmmss}{fileSafeName}.txt");
                await OutputWriter.WriteTextToFileAsync(originalCommand + errorOutputSpacing + errors, path);
                Console.WriteLine("Errors were logged to " + path);
            }
            catch
            {
                Console.Error.WriteLine("The following errors occurred and we were unabled to write a log for them.");
                Console.Error.WriteLine(originalCommand);
                Console.Error.WriteLine(errors);
            }

            Console.WriteLine();
            Console.WriteLine($"Check out {repoUrl}/issues for existing reports of this error and maybe a solution or work-around."
                + " If you want to report it, you can do that there to. Please make sure to supply the error details and example input. Thanks!");
        }

        #region HelpText Option order
        private static int CompareOptions(ComparableOption a, ComparableOption b)
            => ScoreOption(a) < ScoreOption(b) ? -1 : 1;

        private static int ScoreOption(ComparableOption option)
            => (option.IsValue ? -100 : 100) + (option.Required ? -10 : 10) + option.Index;
        #endregion
    }
}