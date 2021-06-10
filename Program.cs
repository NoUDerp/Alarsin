#nullable enable

using System;
using SteamWebAPI2.Utilities;
using System.Timers;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using CommandLine;
using Discord;
using Discord.WebSocket;
using System.Linq;
using System.Text.RegularExpressions;
using SteamWebAPI2.Interfaces;
using System.Net.Http;
using System.Collections.Generic;
using Steam.Models.SteamCommunity;

namespace Alarsin
{
    class Program
    {
        #region "Daemon command line verb"
        /// <summary> Default command line action </summary>
        [Verb("daemon", true, HelpText = "Run daemon", Hidden = false)]
        public class Daemon
        {
            /// <summary> Steam API key </summary>
            [Option('s', "steamapi", Required = true, HelpText = "Steam API key")]
            public string SteamAPIKey { get; set; } = "";

            /// <summary> Discord API Bot Token </summary>
            [Option('d', "discordapi", Required = true, HelpText = "Discord API Token")]
            public string DiscordToken { get; set; } = "";

            /// <summary> Name of the management role </summary>
            [Option('r', "role", Required = false, HelpText = "Alarsin admin role")]
            public string Role { get; set; } = "Friends of Alarsin";
        }
        #endregion

        #region "Logging"
        /// <summary> Write to log asynchronously </summary>
        private static async Task LogAsync(string Content, string Context = "") =>
            await Task.Run(() =>
                Console.WriteLine(Context.Length == 0 ? Content : $"{Context}: {Content}")
            );
        #endregion

        #region "Regular expressions"
        private readonly static Regex parse = new(@"(?:^|\b)(?:(?:[❶❷❸❹❺❻❼❽❾❿][📅🝰⏱]?|🕐|🕑|🕒|🕓|🕔|🕕|🕖|🕗|🕘|🕙|🕚|🕛)[📅🝰⏱]? ?)?(?:\*\*(?<name>.*?)\*\* )?\(?(?<id>\d+)\)?(?<state>: Online|Offline|-)?( \((?<game>.*?)\))?([\s\>]+(?:\|\|)?(?:(?:by|for) (?<follower>.*?), )?(?:(?<date>\w\w\w \d+, \d\d\d\d))(?: \((?<lastnamechange>[A-Z0-9]+)\))?(?:\: \*(?<comment>[^\*]+)\*)?(?:\|\|)?)?(\b|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled | RegexOptions.Multiline);
        private readonly static Regex command = new(@"(?:^|\b)(?<command>add|delete|remove|\-|\+|update)(?:(?:\b|\W)*(?:https?://(?:www\.)?steamcommunity.com/profiles/)?(?<id>\d{17})/?)+(?:\s+(?<comment>.+))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        #endregion

        #region "State"
        /// <summary> Internal state </summary>
        private static ConcurrentDictionary<(IGuild, IChannel), ConcurrentDictionary<UInt64, FriendState>> Tracker { get; set; } = new ConcurrentDictionary<(IGuild, IChannel), ConcurrentDictionary<ulong, FriendState>>();

        /// <summary> Timer events for refreshing </summary>
        private static ConcurrentDictionary<(IGuild, IChannel), Timer> Timers { get; } = new();

        /// <summary> Get initial state (and add a timer) </summary>
        private static async Task<ConcurrentDictionary<ulong, FriendState>> GetStateAsync(IGuild Guild, ITextChannel Channel, string Role, SteamUser Steam) =>
            await Task.Run(() => Tracker.GetOrAdd((Guild, Channel), _ =>
                {
                    CreateTimerAsync(Guild, Channel, Role, Steam).Wait();
                    return new ConcurrentDictionary<ulong, FriendState>();
                }));

        /// <summary> Create an updating timer to refresh tracking for this guild+channel </summary>
        private static async Task CreateTimerAsync(IGuild Guild, ITextChannel Channel, string Role, SteamUser Steam) =>
            await Task.Run(() => Timers.GetOrAdd((Guild, Channel), _ =>
                new(5 * 1000 * 60) { Enabled = true }).
                    Elapsed += async (object sender, ElapsedEventArgs e) =>
                    {
                        try { await UpdateTimer(Guild, Channel, Role, Steam); }
                        catch (Exception E) { await LogAsync(E.ToString(), "Update timer"); }
                    });

        /// <summary> Convert date to a string (compressed) </summary>
        private static string ConvertTimestampToString(DateTime Time) => ((Time.Ticks - new DateTime(2021, 1, 1).Ticks) >> 24).ToString("X");

        /// <summary> Convert string to a date (decompressed) </summary>
        private static DateTime ConvertStringToDateTime(String Time) => new DateTime(new DateTime(2021, 1, 1).Ticks + (long.Parse(Time, System.Globalization.NumberStyles.HexNumber) << 24));

        /// <summary> Refresh tracking in guild+channel </summary>
        private static async Task UpdateTimer(IGuild Guild, ITextChannel Channel, string Role, SteamUser Steam)
        {
            if ((await Guild.GetChannelsAsync(CacheMode.AllowDownload)).Contains(Channel as SocketGuildChannel))
                try { await UpdateGuildChannel(Guild, Channel, Role, Steam); }
                catch (Exception E) { await LogAsync($"{E}", "Discord"); }
            else if (Timers.TryRemove((Guild, Channel), out Timer? Timer) && Timer != null)
                Timer.Stop();
        }

        private static async Task<IReadOnlyCollection<IMessage>?> GetPinnedMessagesAsync(ITextChannel Channel, IUser current_user)
        {
            try { return (await Channel.GetPinnedMessagesAsync()).Where(x => x.Author != null && x.Author == current_user).OrderBy(x => x.CreatedAt).ToList(); }
            catch (Exception E)
            {
                await LogAsync($"Error getting pinned messages in #{Channel.Name} as {current_user.Username}: {E}", "Discord");
                return null;
            }
        }

        /// <summary> Save state </summary>
        private static async Task SaveStateAsync(IGuild Guild, ITextChannel Channel, string Role, SteamUser Steam)
        {
            try
            {
                List<System.Text.StringBuilder> states = new();

                var States = await GetStateAsync(Guild, Channel, Role, Steam);
                if (States.Count == 0)
                    return;

                foreach (var t in (States).OrderBy(x => x.Value.Name, StringComparer.InvariantCultureIgnoreCase).Select(x => x.Value != null ? x.Value.ToString(x.Key) : x.Key.ToString()))
                {
                    if (states.Count == 0 || states.Last().Length + 1 + t.Length > 2000)
                        states.Add(new());

                    if (states.Last().Length > 0)
                        states.Last().Append('\n').Append(t);
                    else
                        states.Last().Append(t);
                }

                if (states.Count == 0)
                    if (Timers.TryRemove((Guild, Channel), out Timer? old) && old != null)
                        old.Stop();

                states.Reverse(); // for alphabetical ordering in the pinned list

                var current_user = await Guild.GetCurrentUserAsync(options: new RequestOptions() { RetryMode = RetryMode.AlwaysRetry });
                var previous_states = await GetPinnedMessagesAsync(Channel, current_user);

                if (previous_states != null)
                {
                    var prev = previous_states.ToList();
                    await previous_states.Skip(states.Count).ToAsyncEnumerable().ForEachAsync(async (x) => { try { await x.DeleteAsync(new RequestOptions() { RetryMode = RetryMode.AlwaysRetry }); } catch (Exception) { } });
                    for (int i = 0; i < states.Count; i++)
                    {
                        var last_state = i < previous_states.Count ? prev[i] : null;

                        if (last_state == null)
                        {
                            try
                            {
                                var message = await Channel.SendMessageAsync(states[i].ToString(), options: new RequestOptions() { RetryMode = RetryMode.AlwaysRetry });
                                if (!message.IsPinned)
                                {
                                    try { await message.PinAsync(new RequestOptions() { RetryMode = RetryMode.AlwaysRetry }); }
                                    catch (Exception E)
                                    {
                                        try { await LogAsync($"Could not pin state message: {E}", "Discord"); }
                                        catch (Exception)
                                        { }
                                    }
                                }
                            }
                            catch (Exception E)
                            {
                                try { await LogAsync($"Could not set initial state message on {Guild.Name} in {Channel.Name}: {E}", "Discord"); }
                                catch (Exception)
                                { }
                            }
                        }
                        else if (last_state != null)
                            await Channel.ModifyMessageAsync(last_state.Id, m => m.Content = states[i].ToString(), new RequestOptions() { RetryMode = RetryMode.AlwaysRetry });
                    }
                }
            }
            catch (Exception E)
            {
                await LogAsync($"Failed to save state, exited early: {E}");
            }
        }

        /// <summary> Load state </summary>
        private static async Task LoadStateAsync(SocketGuild Guild, string Role, SteamUser Steam)
        {
            var current_user = Guild.CurrentUser;

            foreach (var Channel in Guild.TextChannels.Where(c => current_user.GetPermissions(c).ViewChannel))
            {
                var state = await GetStateAsync(Guild, Channel, Role, Steam);

                await foreach (var x in GetPinnedMessagesAsync(Channel, current_user).ToAsyncEnumerable())
                    if (x != null)
                        foreach (var s in x.Where(m => m.Author == Guild.CurrentUser))
                            await foreach (var (ID, State) in FriendState.ParseStates(s.Content).ToAsyncEnumerable())
                                state.TryAdd(ID, State);

                await UpdateGuildChannel(Guild, Channel, Role, Steam);
                await SaveStateAsync(Guild, Channel, Role, Steam);
            }
        }

        /// <summary> Friend state in memory </summary>
        private class FriendState
        {
            /// <summary> Constructor </summary>
            public FriendState(ISteamWebResponse<PlayerSummaryModel> playerSummaryResponse, string User, string? Date, DateTime? LastNameChange, string? Comment)
            {
                this.LastUpdate = DateTime.Now;
                this.Online = playerSummaryResponse.Data.UserStatus == Steam.Models.SteamCommunity.UserStatus.InGame;
                this.Name = playerSummaryResponse.Data.Nickname ?? "";
                this.Game = playerSummaryResponse.Data.PlayingGameName ?? "";
                this.Date = Date ?? DateTime.Now.ToString("MMM d, yyyy");
                this.LastNameChange = LastNameChange ?? new DateTime(2021, 1, 1);
                this.Follower = User;
                this.Comment = Comment ?? "";
            }

            /// <summary> Constructor </summary>
            public FriendState(string Game, string Name, bool Online, string Follower, string Date, DateTime LastNameChange, string Comment)
            {
                this.Game = Game;
                this.Name = Name;
                this.Online = Online;
                this.Follower = Follower;
                this.Date = Date;
                this.LastNameChange = LastNameChange;
                this.Comment = Comment;
            }

            /// <summary> The person who added this ID </summary>
            public string Follower { get; set; } = "";

            /// <summary> Last update of friend status/name </summary>
            public DateTime LastUpdate { get; set; } = DateTime.MinValue;

            /// <summary> Is friend public and online </summary>
            public bool Online { get; set; } = false;

            /// <summary> Friend's nickname </summary>
            public string Name { get; set; } = "";


            /// <summary> Friend's comment </summary>
            public string Comment { get; set; } = "";


            /// <summary> Game friend is playing publicly </summary>
            public string Game { get; set; } = "";

            public string Date { get; set; } = "";

            public DateTime LastNameChange { get; set; } = new DateTime(2021, 1, 1);

            /// <summary> Leading character </summary>
            private static string Lead(TimeSpan Time) =>
                Time.TotalHours switch
                {
                    double hours when hours < 12 => "🕛🕐🕑🕒🕓🕔🕕🕖🕗🕘🕙🕚".Substring((int)hours * 2, 2) + "⏱ ",
                    double hours when hours > 12 && hours < 24 * 10 => "❶❷❸❹❺❻❼❽❾❿".Substring((int)((hours + 12) / 24) - 1, 1) + "⏱ ",// + "📅",
                    _ => ""
                };

            /// <summary> Format state into a string </summary>
            /// <param name="ID">Steam ID</param>
            public string ToString(UInt64 ID) =>
                Lead(DateTime.UtcNow - LastNameChange) +
                ((Name.Length > 0 ? $"**{Name}** ({ID})" : ID.ToString()) + $"{ (Online ? ": Online" : "") }") +
                (Game.Trim().Length > 0 ? $" ({ Game.Trim() })" : "") +
                "\n> ||" +
                (Follower.Length > 0 ? $"for {Follower}, " : "") +
                $"{Date} ({ConvertTimestampToString(LastNameChange)})" +
                (Comment != "" ? $": *{Comment}*" : "") +
                "||";

            /// <summary> Parse a string into states </summary>
            public static IEnumerable<(UInt64 ID, FriendState State)> ParseStates(string Input) =>
                parse.Matches(Input).Select(match =>
                    (
                        ID: UInt64.Parse(match.Groups["id"].Value),
                        State: new FriendState(
                            match.Groups.TryGetValue("game", out Group? game) && game != null ? game.Value : "",
                            match.Groups.TryGetValue("name", out Group? name) && name != null ? name.Value : "",
                            match.Groups.TryGetValue("state", out Group? state) && state != null && state.Value == "Online",
                            match.Groups.TryGetValue("follower", out Group? follower) && follower != null ? follower.Value : "",
                            match.Groups.TryGetValue("date", out Group? date) && date != null ? date.Value : "",
                            match.Groups.TryGetValue("lastnamechange", out Group? lastnamechange) && lastnamechange != null && lastnamechange.Value != "" ? ConvertStringToDateTime(lastnamechange.Value) : new DateTime(2021, 1, 1),
                            match.Groups.TryGetValue("comment", out Group? comment) && comment != null && comment.Value != "" ? comment.Value.Replace("*", "") : ""
                        )
                    )
                );
        }
        #endregion

        #region "Steam User API"
        /// <summary> Update user data by querying steam </summary>
        private static async Task<FriendState?> RefreshFriend(SteamUser SteamInterface, UInt64 ID, string Follower, string? Date, DateTime? LastNameChange, string? PreviousUsername, string? Comment)
        {
            var friend = await SteamInterface.GetPlayerSummaryAsync(ID);
            if (friend == null)
                return null;
            return new(friend, Follower, Date, PreviousUsername != null && !PreviousUsername.Equals(friend.Data.Nickname, StringComparison.InvariantCultureIgnoreCase) ? DateTime.UtcNow : (LastNameChange ?? new DateTime(2021, 1, 1)), Comment ?? "");
        }

        /// <summary> Update the watch list of a channel from a guild </summary>
        private static async Task UpdateGuildChannel(IGuild Guild, ITextChannel Channel, string Role, SteamUser Steam)
        {
            try
            {
                var state = await GetStateAsync(Guild, Channel, Role, Steam);
                await foreach (var y in state.ToAsyncEnumerable())
                    if ((DateTime.Now - y.Value.LastUpdate).TotalMinutes > 5)
                        try
                        {
                            var Update = await RefreshFriend(Steam, y.Key, y.Value.Follower, y.Value.Date, y.Value.LastNameChange, y.Value.Name, y.Value.Comment);
                            if (Update != null)
                                state.TryUpdate(y.Key, Update, y.Value);
                            else
                                state.TryRemove(y.Key, out var _);
                        }
                        catch (Exception E) { await LogAsync($"Failed to update ID {y.Key}: {E.Message}", "Error"); }
                await SaveStateAsync(Guild, Channel, Role, Steam);
            }
            catch (Exception E)
            {
                await LogAsync(E.ToString(), "Steam");
            }
        }
        #endregion

        #region "Events"
        /// <summary> Method to run when a guild becomes available (added, connected to, reconnected, etc...) </summary>
        private static Task GuildAvailable(SocketGuild guild, string Role, SteamUser steam)
        {
            Task.Run(async () =>
            {
                try
                {
                    if (!guild.Roles.Any(role => role.Name.Equals(Role, StringComparison.InvariantCultureIgnoreCase)))
                        await guild.CreateRoleAsync(Role, permissions: null, color: null, isHoisted: false, options: null);

                }
                catch (Exception E) { await LogAsync($"Failed to add role {Role} on {guild.Name}: {E.Message}", "Error"); }
                await LoadStateAsync(guild, Role, steam);
            });
            return Task.CompletedTask;
        }

        public enum Reaction
        {
            None,
            OK,
            Failed,
            Partial
        }


        /// <summary> Method to run when a message is received </summary>
        private static Task IncomingMessage(IMessage message, string Role, SteamUser steam)
        {
            Task.Run(async () =>
            {
                IUser current_user = await (message.Channel as ITextChannel ?? throw new InvalidCastException()).Guild.GetCurrentUserAsync();
                if (message.Type == MessageType.ChannelPinnedMessage && message.Author == current_user)
                {
                    try { await message.DeleteAsync(new RequestOptions() { RetryMode = RetryMode.AlwaysRetry }); }
                    catch (Exception) { }
                    return;
                }

                if (message.Author is SocketGuildUser author)
                {
                    var AuthorizedHunter = author.Roles.Any(x => x.Name.Equals(Role, StringComparison.InvariantCultureIgnoreCase));
                    var DirectMessage = (message.MentionedUserIds.Contains(author.Guild.CurrentUser.Id) || message.MentionedRoleIds.Any(role => author.Guild.Roles.Any(x => x.Id == role)));
                    var Command = command.IsMatch(message.Content);

                    if (DirectMessage)
                    {
                        if (Command && !AuthorizedHunter)
                            await SendTemporaryMessageAsync($"You must be a member of the @{Role} role to modify the watch list, {MentionUtils.MentionUser(message.Author.Id)}.", message.Channel as ITextChannel ?? throw new Exception("Incoming message on non-text channel"), 2 * 60 * 1000);

                        else if (AuthorizedHunter && !Command)
                            await SendTemporaryMessageAsync($"{MentionUtils.MentionUser(message.Author.Id)}, invalid command. Try this format:\n\n**(add|remove|delete|update) <steam ID> [<steam ID ...>] [comment]**\n\n*Examples:*```@{current_user.Username} add ################# comment\n@{current_user.Username} add ################# ################# ################# wasp cheese\n@{current_user.Username} update ################# -\n@{current_user.Username} delete #################```", message.Channel as ITextChannel ?? throw new Exception("Incoming message on non-text channel"), 2 * 60 * 1000);

                        else if (AuthorizedHunter && Command)
                        {
                            var state = await GetStateAsync(author.Guild, message.Channel as ITextChannel ?? throw new InvalidCastException("Cannot convert channel to a text channel"), Role, steam);
                            await foreach (Match m in command.Matches(message.Content).ToAsyncEnumerable())
                                try
                                {
                                    Reaction Result = Reaction.None;
                                    switch (m.Groups["command"].Value.ToLowerInvariant())
                                    {
                                        case "-":
                                        case "remove":
                                        case "delete":
                                            {
                                                var IDs = m.Groups["id"].Captures;
                                                int processed = 0;
                                                await foreach (var ID in IDs.ToAsyncEnumerable().Where(x => ulong.TryParse(x.Value, out var _)).Select(x => ulong.Parse(x.Value)))
                                                    if (state.TryRemove(ID, out FriendState _))
                                                    {
                                                        processed++;
                                                        await LogAsync($"Removed steam ID {ID} from watch list for {author.Guild.Name}");
                                                    }
                                                Result = processed == IDs.Count ? Reaction.OK : Reaction.Partial;
                                            }
                                            break;
                                        case "add":
                                        case "update":
                                        case "":
                                        case "+":
                                            {
                                                var IDs = m.Groups["id"].Captures;
                                                int processed = 0;
                                                await foreach (var ID in IDs.ToAsyncEnumerable().Where(x => ulong.TryParse(x.Value, out var _)).Select(x => ulong.Parse(x.Value)))
                                                {
                                                    var user = await RefreshFriend(steam, ID, author.Username, null!, null!, null!, m.Groups.ContainsKey("comment") && m.Groups["comment"] != null ? m.Groups["comment"].Value.Replace("*", "") : "");
                                                    if (user != null)
                                                    {
                                                        state.AddOrUpdate(ID, user,
                                                            (x, y) => new FriendState(user.Game, user.Name, user.Online, user.Comment != "" ? user.Follower : y.Follower, user.Date, y.LastNameChange, user.Comment == "-" ? "" : (user.Comment != "" ? user.Comment : y.Comment))
                                                            );
                                                        await LogAsync($"Added steam ID {ID} to watch list for {author.Guild.Name}");
                                                        processed++;
                                                    }
                                                    else
                                                        state.TryRemove(ID, out var _);
                                                }
                                                Result = processed == IDs.Count ? Reaction.OK : Reaction.Partial;
                                            }
                                            break;
                                    }

                                    if (Result == Reaction.Failed)
                                        throw new Exception();

                                    await message.AddReactionAsync(new Emoji(Result == Reaction.OK ? "✅" : "⚠"));
                                    new Timer(1000 * 60 * 1) { Enabled = true }.Elapsed += async (object sender, ElapsedEventArgs e) =>
                                    {
                                        try { await message.DeleteAsync(new RequestOptions() { RetryMode = RetryMode.AlwaysRetry }); }
                                        catch (Exception) { }
                                    };
                                }
                                catch (Exception E)
                                {
                                    await LogAsync(E.ToString(), "Discord");
                                    try { await message.AddReactionAsync(new Emoji("❌")); }
                                    catch (Exception) { }
                                }

                            await SaveStateAsync(author.Guild, message.Channel as ITextChannel ?? throw new NullReferenceException("message.Channel is not a text channel"), Role, steam);
                        }
                    }
                }
            });
            return Task.CompletedTask;
        }

        /// <summary> Send a message to a channel and delete it after a time </summary>
        private static async Task SendTemporaryMessageAsync(string Message, ITextChannel Channel, int Timeout)
        {
            var message = await Channel.SendMessageAsync(Message);
            new Timer(Timeout) { Enabled = true }.Elapsed += async (object sender, ElapsedEventArgs e) =>
            {
                try { await message.DeleteAsync(new RequestOptions() { RetryMode = RetryMode.AlwaysRetry }); }
                catch (Exception) { }
            };
        }

        /// <summary> Close a guild </summary>
        private static async Task CloseGuild(SocketGuild g)
        {
            await foreach (var c in g.Channels.ToAsyncEnumerable())
            {
                if (Timers.TryRemove((g, c), out Timer? timer) && timer != null)
                    timer.Stop();
                Tracker.TryRemove((g, c), out var _);
            }
        }

        #endregion

        #region "Main"
        /// <summary> Main controller and entrypoint </summary>
        static async Task Main(string[] args) => await Parser.Default.ParseArguments<Daemon>(args).
            WithParsedAsync(async (Daemon options) =>
            {
                var webInterfaceFactory = new SteamWebInterfaceFactory(options.SteamAPIKey);
                var steam = webInterfaceFactory.CreateSteamWebInterface<SteamUser>(new HttpClient());

                var client = new DiscordSocketClient(new DiscordSocketConfig() { });

                client.Log += async (LogMessage msg) => await LogAsync(msg.Message, "Discord");
                client.GuildAvailable += g => GuildAvailable(g, options.Role, steam);
                client.MessageReceived += m => IncomingMessage(m, options.Role, steam);
                client.MessageUpdated += async (Cacheable<IMessage, ulong> arg1, SocketMessage arg2, ISocketMessageChannel arg3) =>
                {
                    try
                    {
                        await IncomingMessage(await arg1.DownloadAsync(), options.Role, steam);
                    }
                    catch (Exception E)
                    {
                        await LogAsync(E.ToString(), "Discord");
                    }
                };
                client.GuildUnavailable += CloseGuild;

                await client.LoginAsync(TokenType.Bot, options.DiscordToken);
                await client.StartAsync();
                await Task.Delay(-1);
            });

        #endregion
    }
}
