using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Reflection;
using SteamKit2;
using SteamKit2.Authentication; /// brings in steam login
using SteamKit2.Internal; /// brings in protobuf client messages
using SteamKit2.GC; /// brings in the GC related classes
using SteamKit2.GC.Dota.Internal; /// brings in dota specific protobuf messages like CMsgDOTAMatch
using SteamKit2.Discovery;
using ScottPlot;

/// AveYo: adapted from SteamKit2 Samples
class Program {
    const int APPID = 570; /// dota2's appid
	static public List<CMsgDOTAGetPlayerMatchHistoryResponse.Match> Matches { get; private set; }
    static SteamClient steamClient; static CallbackManager manager; static SteamGameCoordinator coordinator;
    static SteamUser steamUser;
    static string user; static string pass; static string arg0; static string arg1;
    static ulong matches_start_at_id, latest_saved_mid;
    static int matches_requested;
    static int matches_remaining;
    static int matches_count;
    private static uint account;
    static bool isRunning, haveConnected;
    static string dota_cfg;

    [STAThread]
    static int Main(string[] args) {
        Console.WriteLine("Usage:");
        Console.WriteLine("ShowMMR <steam_user_name> <matches_count>");
        Console.WriteLine("Pass matches_count = 0 to only display the mmr graph.");
        Console.WriteLine("Make sure to close Dota before running this.");
        Console.WriteLine();

        if (args.Length == 0) {
            Console.Error.Write("Steam login user: ");
            arg0 = Console.ReadLine();
            arg0 = arg0.Trim();
        }
        user = args.Length == 0 ? arg0 : args[0];

        /// do not prompt for pass if user credentials are cached
        var cached_pass = File.Exists(user + ".auth");

        if (!cached_pass) {
            Console.Error.Write("Steam login pass: "); /// cached to user.auth, next time can type anything

            /// stackoverflow.com/questions/3404421/password-masking-console-application @ CraigTP
            var masked = string.Empty;
            ConsoleKey key;
            do {
                var keyInfo = Console.ReadKey(intercept: true);
                key = keyInfo.Key;

                if (key == ConsoleKey.Backspace && masked.Length > 0) {
                    Console.Error.Write("\b \b");
                    masked = masked.Substring(0, masked.Length - 1);
                }
                else if (!char.IsControl(keyInfo.KeyChar)) {
                    Console.Error.Write("*");
                    masked += keyInfo.KeyChar;
                }
            } while (key != ConsoleKey.Enter);
            Console.Error.WriteLine();
            arg1 = masked;
        }
        pass = cached_pass ? "gaben" : arg1;


        string mc;
        if (args.Length >= 2) 
            mc = args[1];
        else {
            Console.Error.Write("Number of matches to fetch: ");
            mc = Console.ReadLine();
        }
        
        if (!int.TryParse(mc, out matches_count)) {
            Console.Error.WriteLine("Invalid 2nd arg matches_count! Try 20 - 360");
            return 1;
        }

        // Parsing done. Now we can start the Steam connection.
        // ----------------------------------------------------------------------------------------------

        matches_requested = 20;
        matches_remaining = matches_count;
        account = 0;

        var cellid = 0u;
        // if we've previously connected and saved our cellid, load it.
        if (File.Exists("cellid.txt")) {
            if (!uint.TryParse(File.ReadAllText("cellid.txt"), out cellid)) {
                Console.WriteLine("Error parsing cellid from cellid.txt. Continuing with cellid 0.");
                cellid = 0;
            }
            else {
                Console.WriteLine($"Using persisted cell ID {cellid}");
            }
        }
        var configuration = SteamConfiguration.Create(b =>
            b.WithCellID(cellid)
                .WithServerListProvider(new FileStorageServerListProvider("servers_list.bin")));

        // create our steamclient instance
        steamClient = new SteamClient(configuration);
        /// create the callback manager which will route callbacks to function calls
        manager = new CallbackManager(steamClient);

        /// get the steamuser handler, which is used for logging on after successfully connecting
        steamUser = steamClient.GetHandler<SteamUser>();
        /// get the GC
        coordinator = steamClient.GetHandler<SteamGameCoordinator>();

        /// register a few callbacks we're interested in
        /// these are registered upon creation to a callback manager, which will then route the callbacks
        /// to the functions specified
        manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        /// GC message
        manager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);

        isRunning = true;
        haveConnected = false;

        Console.WriteLine("Connecting to Steam...");

        /// initiate the connection
        steamClient.Connect();

        /// create our callback handling loop
        while (isRunning){
            /// in order for the callbacks to get routed, they need to be handled by the manager
            manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
        }

        Console.WriteLine("Done fetching matches!");

        /// display user steam id
        var account_id = account;

        if (Matches == null || account_id == 0) {
            Console.WriteLine("No results to display for {0}", account_id);
            File.Delete(user + ".auth");
            System.Environment.Exit(1);
        }

        ScottPlot.Plot myPlot = new();

        int dd_cnt = 0, dd_won = 0;
        int mmr_inflated = 0;
        uint crownfall_start = ConvertToUnixTime(new DateTime(2024, 4, 19, 0, 0, 0, DateTimeKind.Utc));

        var mmr_history = new System.Text.StringBuilder();
        mmr_history.AppendFormat("Date,Unix time,MatchID,Start MMR,Rank Change\r\n");

        for (int x = 0; x < Matches.Count; x++) {
            var m = Matches[x]; /// CMsgDOTAMatch
            mmr_history.AppendFormat("{0},{1},{2},{3},{4}\r\n",
            FormatTime(m.start_time), m.start_time, m.match_id, m.previous_rank, m.rank_change);

            if (Math.Abs(m.rank_change) > 40 && m.start_time > crownfall_start) {
                dd_cnt++;
                if (m.rank_change > 0) dd_won++;
                mmr_inflated += m.rank_change/2;
            }
        }

        Console.WriteLine("Double down accuracy: {0} / {1} = {2}% (estimated using matches where |rank change| > 40)", dd_won, dd_cnt, 100 * dd_won / dd_cnt);
        Console.WriteLine("MMR inflated by: {0} (approximate lower bound)", mmr_inflated);

        DateTime[] dataX = Matches.Select(m => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(m.start_time).ToLocalTime()).ToArray();
        uint[] dataY = Matches.Select(m => m.previous_rank).ToArray();

        myPlot.Add.Scatter(dataX, dataY);
        myPlot.Axes.DateTimeTicksBottom();
        myPlot.XLabel("Time");
        myPlot.YLabel("MMR");

        File.WriteAllText(dota_cfg, mmr_history.ToString());

        ScottPlot.WinForms.FormsPlotViewer.Launch(myPlot);

        return 0;
    }
    static uint ConvertToUnixTime(DateTime dateTime) {
        return (uint)(dateTime.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
    }

    static String FormatTime(uint unixTime) {
        
        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime().AddSeconds(unixTime).ToString("yyyy-MM-dd HH:mm:ss");
    }

    static async void OnConnected(SteamClient.ConnectedCallback callback) {
        Console.WriteLine("Connected to Steam! Logging in '{0}'...", user);
        haveConnected = true;

        var cached_auth = user + ".auth";

        if (File.Exists(cached_auth)) {
            var reAccessToken = File.ReadAllText(cached_auth, System.Text.Encoding.ASCII);
            /// Logon to Steam with the access token we have saved
            steamUser.LogOn(new SteamUser.LogOnDetails {
                Username = user,
                AccessToken = reAccessToken,
            });

        }
        else {
            try {
                /// Begin authenticating via credentials
                var authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                    new AuthSessionDetails {
                        Username = user,
                        Password = pass,
                        IsPersistentSession = false,
                        Authenticator = new UserConsoleAuthenticator(),
                    }
                );

                /// Starting polling Steam for authentication response
                var pollResponse = await authSession.PollingWaitForResultAsync();

                Console.WriteLine(pollResponse.AccountName);
                Console.WriteLine(pollResponse.AccessToken);
                File.WriteAllText(cached_auth, pollResponse.RefreshToken, System.Text.Encoding.ASCII);

                /// Logon to Steam with the access token we have received
                /// Note that we are using RefreshToken for logging on here
                steamUser.LogOn(new SteamUser.LogOnDetails {
                    Username = pollResponse.AccountName,
                    AccessToken = pollResponse.RefreshToken,
                });

            }
            catch { Console.WriteLine("Unable to logon to Steam with username '{0}'", user); isRunning = false; }
        }
    }

    static void OnDisconnected(SteamClient.DisconnectedCallback callback) {
        Console.WriteLine("Disconnected from Steam");
        if (haveConnected)
            isRunning = false;
        else {
            Console.WriteLine("Trying again in 5...");
            Thread.Sleep(TimeSpan.FromSeconds(5));
            steamClient.Connect();
        }
    }

    static void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
        if (callback.Result != EResult.OK) {
            Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

            isRunning = false;
            return;
        }
        // save the current cellid somewhere. if we lose our saved server list, we can use this when retrieving
        // servers from the Steam Directory.
        File.WriteAllText("cellid.txt", callback.CellID.ToString());

        account = steamUser.SteamID.AccountID;

        // now that the account id is available, we need to get this done
        dota_cfg = "mmr_hist_" + account.ToString() + ".csv";
        Matches = ParseCSV(dota_cfg);
        latest_saved_mid = Matches.Count > 0 ? Matches[0].match_id : 0;

        if (matches_count == 0) {
            Console.WriteLine("Skipping GC login.");
            isRunning = false;
            return;
        }

        /// at this point, we're able to perform actions on Steam
        Console.WriteLine("Logged in! Launching DOTA...");

        /// we've logged into the account
        /// now we need to inform the steam server that we're playing dota (in order to receive GC messages)

        /// steamkit doesn't expose the "play game" message through any handler, so we'll just send the message manually
        var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

        playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
            game_id = new GameID(APPID), /// or game_id = APPID,
		});

        /// send it off
        /// notice here we're sending this message directly using the SteamClient
        steamClient.Send(playGame);

        /// delay a little to give steam some time to establish a GC connection to us
        Thread.Sleep(5000);

        /// inform the dota GC that we want a session
        var clientHello = new ClientGCMsgProtobuf<SteamKit2.GC.Dota.Internal.CMsgClientHello>(
            (uint)EGCBaseClientMsg.k_EMsgGCClientHello);
        clientHello.Body.engine = ESourceEngine.k_ESE_Source2;
        coordinator.Send(clientHello, APPID);
    }

    static void OnLoggedOff(SteamUser.LoggedOffCallback callback) {
        Console.WriteLine("Logged off of Steam: {0}", callback.Result);
    }

    /// called when a gamecoordinator (GC) message arrives
    /// these kinds of messages are designed to be game-specific
    /// in this case, we'll be handling dota's GC messages
    static void OnGCMessage(SteamGameCoordinator.MessageCallback callback) {
        /// setup our dispatch table for messages
        /// this makes the code cleaner and easier to maintain
        var messageMap = new Dictionary<uint, Action<IPacketGCMsg>> {
                { ( uint )EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { ( uint )EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistoryResponse, OnMatchHistory },
            };

        Action<IPacketGCMsg> func;
        if (!messageMap.TryGetValue(callback.EMsg, out func)) {
            /// this will happen when we recieve some GC messages that we're not handling
            /// this is okay because we're handling every essential message, and the rest can be ignored
            return;
        }

        func(callback.Message);
    }

    /// this message arrives when the GC welcomes a client
    /// this happens after telling steam that we launched dota (with the ClientGamesPlayed message)
    /// this can also happen after the GC has restarted (due to a crash or new version)
    static void OnClientWelcome(IPacketGCMsg packetMsg) {
        /// in order to get at the contents of the message, we need to create a ClientGCMsgProtobuf from the packet message we recieve
        /// note here the difference between ClientGCMsgProtobuf and the ClientMsgProtobuf used when sending ClientGamesPlayed
        /// this message is used for the GC, while the other is used for general steam messages
        var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);
        Console.WriteLine("GC is welcoming us. Version: {0}", msg.Body.version);
        /// at this point, the GC is now ready to accept messages from us

        Console.WriteLine("Requesting {0} recent matches history", matches_count);
        fetchMatches();
    }

    static void fetchMatches() {
        matches_requested = Math.Min(20, matches_count);
        matches_remaining -= matches_requested;
        var requestHistory = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistory>(
            (uint)EDOTAGCMsg.k_EMsgDOTAGetPlayerMatchHistory);
        requestHistory.Body.account_id = steamUser.SteamID.AccountID;
        requestHistory.Body.matches_requested = (uint) matches_requested;
        if (matches_start_at_id > 0)
            requestHistory.Body.start_at_match_id = matches_start_at_id;
        coordinator.Send(requestHistory, APPID);
    }

    /// this message arrives after we've requested the details for a match
    static void OnMatchHistory(IPacketGCMsg packetMsg) {
        isRunning = true;
        var msg = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistoryResponse>(packetMsg);

        foreach (var match in msg.Body.matches) {
            if (match.start_time != 0 && match.match_id != 0 && match.rank_change != 0 && match.previous_rank != 0) {
                if (match.match_id == latest_saved_mid) {
                    matches_start_at_id = Matches.Last().match_id;
                    break;
                }
                Matches.Add(match);
                Matches.Sort((x, y) => y.start_time.CompareTo(x.start_time));
            }
            matches_start_at_id = match.match_id;
        }

        if (matches_remaining <= 0 || msg.Body.matches.Count <= 0 || msg.Body.matches.Count < matches_requested) {
            if (msg.Body.matches.Count <= 0) {
                Console.WriteLine("Empty response received!");
            }
            steamClient.Disconnect();
        }
        else {
            Thread.Sleep(1000);
            var start_at_match_id = msg.Body.matches[msg.Body.matches.Count - 1].match_id;
            Console.WriteLine("Matches remaining: {0} start at: {1}", matches_remaining, start_at_match_id);
            fetchMatches();
        }
    }

    static List<CMsgDOTAGetPlayerMatchHistoryResponse.Match> ParseCSV(string filePath) {
        List<CMsgDOTAGetPlayerMatchHistoryResponse.Match> matches = new List<CMsgDOTAGetPlayerMatchHistoryResponse.Match>();

        try {
            using (StreamReader reader = new StreamReader(filePath)) {
                string line;
                reader.ReadLine(); // skip header

                while ((line = reader.ReadLine()) != null) {
                    string[] values = line.Split(',');

                    if (values.Length >= 5) {
                        CMsgDOTAGetPlayerMatchHistoryResponse.Match match = new CMsgDOTAGetPlayerMatchHistoryResponse.Match {
                            start_time = uint.Parse(values[1]),
                            match_id = ulong.Parse(values[2]),
                            previous_rank = uint.Parse(values[3]),
                            rank_change = int.Parse(values[4])
                        };

                        matches.Add(match);
                    }
                }
            }
        } catch (FileNotFoundException) {}

        return matches;
    }
}

