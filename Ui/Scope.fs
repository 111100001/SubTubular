﻿namespace Ui

open System
open System.Linq
open System.Threading
open System.Threading.Tasks
open Avalonia.Animation
open Avalonia.Controls
open Avalonia.Interactivity
open Avalonia.Media
open Fabulous
open Fabulous.Avalonia
open SubTubular
open YoutubeExplode.Videos
open type Fabulous.Avalonia.View

module Scope =
    let private (|IsChannel|IsPlaylist|IsVideos|) (t: Type) =
        match t with
        | _ when t = typeof<ChannelScope> -> IsChannel
        | _ when t = typeof<PlaylistScope> -> IsPlaylist
        | _ when t = typeof<VideosScope> -> IsVideos
        | _ -> failwith ("unknown scope type " + t.FullName)

    let private (|Channel|Playlist|Videos|) (scope: CommandScope) =
        match scope with
        | :? ChannelScope as channel -> Channel channel
        | :? PlaylistScope as playlist -> Playlist playlist
        | :? VideosScope as videos -> Videos videos
        | _ -> failwith $"unsupported {nameof CommandScope} type on {scope}"

    let private (|PlaylistLike|Vids|) (scope: CommandScope) =
        match scope with
        | :? PlaylistLikeScope as playlist -> PlaylistLike playlist
        | :? VideosScope as videos -> Vids videos
        | _ -> failwith $"unsupported {nameof CommandScope} type on {scope}"

    module Alias =
        /// removes title prefix, i.e. everything before the last ':'
        let clean (alias: string) =
            let colon = alias.LastIndexOf(':')

            if colon < 0 then
                alias
            else
                alias.Substring(colon + 1).Trim()

        let label (title: string) alias =
            let cleanTitle = title.Replace(":", "")
            $"{cleanTitle} : {alias}"

    module VideosInput =
        let join values = values |> String.concat "\n"

        let private split (input: string) =
            input.Split('\n', StringSplitOptions.RemoveEmptyEntries) |> Array.map _.Trim()

        let cleanTitle (title: string) = title

        let splitAndClean (input: string) =
            let array = split input |> Array.map Alias.clean
            array.ToList()

        /// splits and partitions the input into two lists:
        /// first the pre-validated, labeled/uncleaned video aliases,
        /// second the unvalidated values considered search terms
        let partition (input: string) =
            split input
            |> List.ofArray
            |> List.partition (fun alias ->
                let value = Alias.clean alias
                VideoId.TryParse(value).HasValue)

    //TODO prevent from running again when updating alias to name plus named id
    type AliasSearch() =
        let mutable searching: CancellationTokenSource = null
        let input = ViewRef<AutoCompleteBox>()
        let heartBeat = ViewRef<Animation>()

        let isRunning () = searching <> null

        let cancel () =
            if isRunning () then
                searching.Cancel()
                searching.Dispose()
                searching <- null


        /// animates the input with the heartBeat until searchToken is cancelled
        let animateInput searchToken =
            heartBeat.Value.IterationCount <- IterationCount.Infinite
            heartBeat.Value.RunAsync(input.Value, searchToken) |> ignore

        let yieldResults (results: YoutubeSearchResult seq) =
            if isRunning () && not searching.Token.IsCancellationRequested then
                cancel () // to stop animation
                let converted = results |> Seq.cast<obj>
                converted
            else
                cancel () // to stop animation
                Seq.empty

        member this.Input = input
        member this.HeartBeat = heartBeat
        member this.Cancel = cancel

        //TODO maybe remember the return value as selected per instance?
        member this.SelectAliases text (result: YoutubeSearchResult) forVideos =
            if forVideos then
                let selection, searchTerms = VideosInput.partition text
                let title = VideosInput.cleanTitle result.Title
                let labeledAlias = Alias.label title result.Id
                selection @ [ labeledAlias ] @ searchTerms |> VideosInput.join
            else
                Alias.label result.Title result.Id

        //TODO heart beat is not stopping in time for videos
        //TODO maybe don't search if the input was selected from a search result before
        member this.SearchAsync (scope: CommandScope) (text: string) (cancellation: CancellationToken) : Task<obj seq> =
            task {
                if input.Value.IsKeyboardFocusWithin then // only start search if input has keyboard focus
                    cancellation.Register(cancel) |> ignore // register cancellation of running search when outer cancellation is requested
                    cancel () // cancel any older running search
                    searching <- new CancellationTokenSource() // and create a new source for this one
                    animateInput (searching.Token) // pass running search token to stop it when the search completes or is cancelled

                    match scope with
                    | Channel _ ->
                        let! channels = Services.Youtube.SearchForChannelsAsync(text, searching.Token)
                        return yieldResults channels
                    | Playlist _ ->
                        let! playlists = Services.Youtube.SearchForPlaylistsAsync(text, searching.Token)
                        return yieldResults playlists
                    | Videos _ ->
                        match VideosInput.partition text with
                        | _, [] -> return []
                        | _, searchTerms ->
                            let! videos =
                                Services.Youtube.SearchForVideosAsync(
                                    searchTerms |> String.concat " or ",
                                    searching.Token
                                )

                            return yieldResults videos
                else
                    return []
            }

    type Model =
        { Scope: CommandScope
          Aliases: string
          AliasSearch: AliasSearch
          ValidationError: string
          ShowSettings: bool
          Added: bool }

    type Msg =
        | AliasesUpdated of string
        | AliasesLostFocus of RoutedEventArgs
        | ValidationSucceeded
        | ValidationFailed of exn
        | ToggleSettings of bool
        | SkipChanged of float option
        | TakeChanged of float option
        | CacheHoursChanged of float option
        | Remove
        | RemoveVideo of string
        | ProgressChanged
        | ProgressValueChanged of float
        | Common of CommonMsg

    type Intent =
        | RemoveMe
        | DoNothing

    let isForVideos model =
        match model.Scope with
        | :? VideosScope as _ -> true
        | _ -> false

    let private remoteValidate model =
        task {
            try
                match model.Scope with
                | Channel channel ->
                    do!
                        RemoteValidate.ChannelsAsync(
                            [| channel |],
                            Services.Youtube,
                            Services.DataStore,
                            CancellationToken.None
                        )
                | Playlist playlist ->
                    do! RemoteValidate.PlaylistAsync(playlist, Services.Youtube, CancellationToken.None)
                | Videos videos ->
                    do!
                        RemoteValidate.AllVideosAsync(
                            videos,
                            Services.Youtube,
                            Services.DataStore,
                            CancellationToken.None
                        )

                return ValidationSucceeded
            with exn ->
                return ValidationFailed exn
        }

    let private validate model =
        let bypass =
            match model.Scope with
            | PlaylistLike pl -> String.IsNullOrWhiteSpace(pl.Alias) || pl.IsValid
            | Vids vs ->
                let remoteValidated = vs.GetRemoteValidated() |> Seq.map _.Id
                let unvalidated = vs.Videos |> Seq.except remoteValidated
                Seq.isEmpty unvalidated

        if bypass then
            model, Cmd.none
        else
            match Prevalidate.Scope model.Scope with
            | null ->
                if model.Scope.IsPrevalidated then
                    model, Cmd.OfTask.msg (remoteValidate model)
                else
                    model, Cmd.none
            | error -> { model with ValidationError = error }, Cmd.none

    let private init scope aliases added =
        { Scope = scope
          Aliases = aliases
          AliasSearch = AliasSearch()
          ValidationError = null
          ShowSettings = false
          Added = added }

    /// Re-creates a scope from a reloaded recent command that was previously executed and kicks off its validation,
    /// returning the new scope model and a Cmd for the running validation
    let recreateRecent (scope: CommandScope) =
        let model = init scope "" false
        validate model

    /// creates a scope on user command, marking it as Added for it to be focused
    let add scopeType =
        let aliases = ""
        let skip = uint16 0
        let take = uint16 50
        let cacheHours = float32 24

        let scope =
            match scopeType with
            | IsChannel -> ChannelScope(aliases, skip, take, cacheHours) :> CommandScope
            | IsPlaylist -> PlaylistScope(aliases, skip, take, cacheHours)
            | IsVideos -> VideosScope(VideosInput.splitAndClean aliases)

        init scope aliases true

    let update msg model =
        match msg with
        | ToggleSettings show -> { model with ShowSettings = show }, Cmd.none, DoNothing

        | AliasesUpdated aliases ->
            { model with
                Added = false
                ValidationError = null
                Aliases = aliases },
            Cmd.none,
            DoNothing

        | AliasesLostFocus _ ->
            let aliases = model.Aliases

            let updatedAliases =
                match model.Scope with
                | PlaylistLike playlist ->
                    playlist.Alias <- Alias.clean aliases
                    aliases
                | Vids vids ->
                    let prevalidated, invalid = VideosInput.partition aliases
                    let missing = prevalidated |> List.map Alias.clean |> List.except vids.Videos

                    if not missing.IsEmpty then
                        vids.Videos.AddRange missing // to have them validated

                    VideosInput.join invalid // only leave invalid ids in the input as search terms

            model.AliasSearch.Cancel() // to avoid population after losing focus
            let model = { model with Aliases = updatedAliases }
            let model, cmd = validate model
            model, cmd, DoNothing

        | ValidationSucceeded -> { model with ValidationError = null }, Cmd.none, DoNothing

        | ValidationFailed exn ->
            { model with
                ValidationError = exn.Message },
            Cmd.none,
            DoNothing

        | SkipChanged skip ->
            match model.Scope with
            | PlaylistLike scope -> scope.Skip <- skip |> Option.defaultValue 0 |> uint16
            | _ -> ()

            model, Cmd.none, DoNothing

        | TakeChanged take ->
            match model.Scope with
            | PlaylistLike scope -> scope.Take <- take |> Option.defaultValue 50 |> uint16
            | _ -> ()

            model, Cmd.none, DoNothing

        | CacheHoursChanged hours ->
            match model.Scope with
            | PlaylistLike scope -> scope.CacheHours <- hours |> Option.defaultValue 24 |> float32
            | _ -> ()

            model, Cmd.none, DoNothing

        | RemoveVideo id ->
            match model.Scope with
            | Vids scope -> scope.Remove(id)
            | _ -> ()

            model, Cmd.none, DoNothing

        | Remove -> model, Cmd.none, RemoveMe
        | ProgressChanged -> model, Cmd.none, DoNothing
        | ProgressValueChanged _ -> model, Cmd.none, DoNothing
        | Common _ -> model, Cmd.none, DoNothing

    let private getAliasWatermark model =
        match model.Scope with
        | Videos _ -> "IDs or URLs; one per line"
        | Playlist _ -> "ID or URL"
        | Channel _ -> "handle, slug, user name, ID or URL"

    let private channelInfo channel =
        TextBlock(Icon.channel + channel).smallDemoted ()

    let getIcon (t: Type) =
        match t with
        | IsVideos -> Icon.video
        | IsPlaylist -> Icon.playlist
        | IsChannel -> Icon.channel

    let displayType (t: Type) withKeyBinding =
        getIcon t
        + if withKeyBinding then "_" else ""
        + match t with
          | IsVideos -> "videos"
          | IsPlaylist -> "playlist"
          | IsChannel -> "channel"

    let private progressText text = TextBlock(text).right().smallDemoted ()

    let private validated thumbnailUrl navigateUrl title channel (scope: CommandScope) progress videoId showThumbnails =
        Grid(coldefs = [ Auto; Auto; Auto; Auto ], rowdefs = [ Auto; Auto ]) {
            match videoId with
            | Some videoId -> Button("❌", RemoveVideo videoId).tooltip("remove this video").gridRowSpan (2)
            | None -> ()

            AsyncImage(thumbnailUrl)
                .tappable(OpenUrl navigateUrl |> Common, "tap to open in the browser")
                .maxHeight(30)
                .gridColumn(1)
                .gridRowSpan(2)
                .isVisible (showThumbnails)

            TextBlock(getIcon (scope.GetType()) + title).gridColumn(2).gridColumnSpan (2)

            match channel with
            | Some channel -> channelInfo(channel).gridColumn(2).gridRow (1)
            | None -> ()

            match progress with
            | Some progress -> progressText(progress).gridRow(1).gridColumn (3)
            | None -> ()
        }

    let private search model showThumbnails =
        let forVideos = isForVideos model

        Grid(coldefs = [ Auto; Star ], rowdefs = [ Auto; Auto ]) {
            Label(displayType (model.Scope.GetType()) false).padding (0)

            let autoComplete =
                AutoCompleteBox(fun text ct -> model.AliasSearch.SearchAsync model.Scope text ct)
                    .minimumPopulateDelay(TimeSpan.FromMilliseconds 300)
                    .onTextChanged(model.Aliases, AliasesUpdated)
                    .onLostFocus(AliasesLostFocus)
                    .minimumPrefixLength(3)
                    .filterMode(AutoCompleteFilterMode.None)
                    .focus(model.Added)
                    .watermark("by " + getAliasWatermark model)
                    .itemSelector(fun enteredText item ->
                        model.AliasSearch.SelectAliases enteredText (item :?> YoutubeSearchResult) forVideos)
                    .itemTemplate(fun (result: YoutubeSearchResult) ->
                        HStack(5) {
                            AsyncImage(result.Thumbnail).isVisible (showThumbnails)

                            VStack(5) {
                                TextBlock(result.Title)

                                if result.Channel <> null then
                                    channelInfo (result.Channel)
                            }
                        })
                    .reference(model.AliasSearch.Input)
                    .animation(
                        // pulses the scale like a heart beat to indicate activity
                        (Animation(TimeSpan.FromSeconds(2.)) {
                            // extend slightly but quickly to get a pulse effect
                            KeyFrame(ScaleTransform.ScaleXProperty, 1.05).cue (0.1)
                            KeyFrame(ScaleTransform.ScaleYProperty, 1.05).cue (0.1)
                            // contract slightly to get a bounce-back effect
                            KeyFrame(ScaleTransform.ScaleXProperty, 0.95).cue (0.15)
                            KeyFrame(ScaleTransform.ScaleYProperty, 0.95).cue (0.15)
                            // return to original size rather quickly
                            KeyFrame(ScaleTransform.ScaleXProperty, 1).cue (0.2)
                            KeyFrame(ScaleTransform.ScaleYProperty, 1).cue (0.2)
                        })
                            .repeatCount(0)
                            .delay(TimeSpan.FromSeconds 1.) // to avoid a "heart attack", i.e. restarting the animation by typing
                            .reference (model.AliasSearch.HeartBeat)
                    )
                    .margin(5, 0, 0, 0)
                    .gridColumn(1)
                    .gridRowSpan (2)

            match model.Scope with
            | Videos videos ->
                progressText(videos.Progress.ToString()).gridRow (1)
                autoComplete.acceptReturn ()
            | _ -> autoComplete
        }

    let private removeBtn () =
        Button("❌", Remove).tooltip ("remove this scope")

    let view model maxWidth showThumbnails =
        VStack() {
            match model.Scope with
            | PlaylistLike playlistLike ->
                HStack(5) {
                    removeBtn ()

                    if playlistLike.IsValid then
                        let validationResult = playlistLike.SingleValidated
                        let playlist = validationResult.Playlist

                        let channel =
                            match playlistLike with
                            | :? PlaylistScope -> Some playlist.Channel
                            | _ -> None

                        validated
                            playlist.ThumbnailUrl
                            validationResult.Url
                            playlist.Title
                            channel
                            playlistLike
                            (Some(model.Scope.Progress.ToString()))
                            None
                            showThumbnails
                    else
                        search model showThumbnails

                    ToggleButton("⚙", model.ShowSettings, ToggleSettings)
                        .tooltip ("toggle settings")

                    (HStack(5) {
                        Label "skip"

                        NumericUpDown(0, float UInt16.MaxValue, Some(float playlistLike.Skip), SkipChanged)
                            .formatString("F0")
                            .tooltip ("number of videos to skip")

                        Label "take"

                        NumericUpDown(0, float UInt16.MaxValue, Some(float playlistLike.Take), TakeChanged)
                            .formatString("F0")
                            .tooltip ("number of videos to search")

                        Label "videos"
                    })
                        .centerHorizontal()
                        .isVisible (model.ShowSettings)

                    (HStack(5) {
                        Label "and look for new ones after"

                        NumericUpDown(0, float UInt16.MaxValue, Some(float playlistLike.CacheHours), CacheHoursChanged)
                            .formatString("F0")
                            .tooltip (
                                "The info which videos are in a playlist or channel is cached locally to speed up future searches."
                                + " This controls after how many hours such a cache is considered stale."
                                + " You may want to adjust this depending on how often new videos are added to the playlist or uploaded to the channel."
                                + Environment.NewLine
                                + Environment.NewLine
                                + "Note that this doesn't figure into the staleness of video data caches."
                                + " Those are not expected to change often (if ever) and are stored and considered fresh until you explicitly clear them."
                            )

                        Label "hours"
                    })
                        .centerHorizontal()
                        .isVisible (model.ShowSettings)
                }

            | Vids videos ->
                let remoteValidated = videos.GetRemoteValidated() |> List.ofSeq

                Grid(coldefs = [ Auto; Auto ], rowdefs = [ Auto; Auto ]) {
                    if remoteValidated.IsEmpty then
                        removeBtn().gridRow (1)
                    else
                        (HWrap() {
                            for validationResult in remoteValidated do
                                let video = validationResult.Video

                                validated
                                    video.Thumbnail
                                    validationResult.Url
                                    video.Title
                                    (Some video.Channel)
                                    model.Scope
                                    None
                                    (Some video.Id)
                                    showThumbnails
                        })
                            .gridColumn(1)
                            (*   *)
                            .maxWidth (maxWidth)

                    (search model showThumbnails).maxWidth(maxWidth).gridColumn(1).gridRow (1)
                }

            TextBlock(model.ValidationError)
                .foreground(Colors.Red)
                .wrap()
                .isVisible (model.ValidationError <> null) // && not model.Scope.IsValid)

            ProgressBar(0, model.Scope.Progress.AllJobs, model.Scope.Progress.CompletedJobs, ProgressValueChanged)
                .onScopeProgressChanged (model.Scope, ProgressChanged)
        }
