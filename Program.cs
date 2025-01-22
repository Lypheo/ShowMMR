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
    static ulong matches_start_at_id = 0, latest_saved_mid;
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
            string[] authFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.auth");
            string accName = authFiles.Length > 0 ? Path.GetFileNameWithoutExtension(authFiles[0]) : "";
            Console.Error.Write("Steam login user: {0}", accName);
            arg0 = Console.ReadLine();
            arg0 = arg0.Trim();
            if (arg0 == "")
                arg0 = accName;
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

        dota_cfg = "mmr_hist_v2_" + user + ".csv";
        Matches = ParseCSV(dota_cfg);
        latest_saved_mid = Matches.Count > 0 ? Matches[0].match_id : 0;

        // Parsing done. Now we can start the Steam connection.
        // ----------------------------------------------------------------------------------------------

        if (matches_count > 0) {
            matches_requested = 20;
            matches_remaining = matches_count;
            account = 0;

            var cellid = 0u;
            // if we've previously connected and saved our cellid, load it.
            if (File.Exists("cellid.txt")) {
                if (!uint.TryParse(File.ReadAllText("cellid.txt"), out cellid)) {
                    Console.WriteLine("Error parsing cellid from cellid.txt. Continuing with cellid 0.");
                    cellid = 0;
                } else {
                    Console.WriteLine($"Using persisted cell ID {cellid}");
                }
            }
            var configuration = SteamConfiguration.Create(b =>
                b.WithCellID(cellid)
                    .WithServerListProvider(new FileStorageServerListProvider("servers_list.bin")));

            steamClient = new SteamClient(configuration);
            manager = new CallbackManager(steamClient);
            steamUser = steamClient.GetHandler<SteamUser>();
            coordinator = steamClient.GetHandler<SteamGameCoordinator>();

            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
            manager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGCMessage);

            isRunning = true;
            haveConnected = false;

            Console.WriteLine("Connecting to Steam...");

            steamClient.Connect();

            while (isRunning) {
                manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }

            Console.WriteLine("Done fetching matches!");

            if (Matches == null || account == 0) {
                Console.WriteLine("Something went wrong. Press any key to exit...");
                Console.ReadKey(true);
                File.Delete(user + ".auth");
                return 1;
            }
        }

        ScottPlot.Plot myPlot = new();

        int dd_cnt = 0, dd_won = 0;
        int mmr_inflated = 0;
        uint crownfall_start = ConvertToUnixTime(new DateTime(2024, 4, 19, 0, 0, 0, DateTimeKind.Utc));

        Dictionary<int, int> heroCounter = new Dictionary<int, int>();
        int partyCounter = 0, soloCounter = 0;

        var mmr_history = new System.Text.StringBuilder();
        mmr_history.AppendFormat("Date,Unix time,MatchID,Solo Queue,HeroID,Start MMR,Rank Change\r\n");

        for (int x = 0; x < Matches.Count; x++) {
            var m = Matches[x]; /// CMsgDOTAMatch
            mmr_history.AppendFormat("{0},{1},{2},{3},{4:D3},{5},{6}\r\n",
            FormatTime(m.start_time), m.start_time, m.match_id, m.solo_rank, m.hero_id, m.previous_rank, m.rank_change);

            if (Math.Abs(m.rank_change) > 40 && m.start_time > crownfall_start) {
                dd_cnt++;
                if (m.rank_change > 0) dd_won++;
                mmr_inflated += m.rank_change/2;
            }

            if (heroCounter.ContainsKey(m.hero_id))
                heroCounter[m.hero_id] += m.rank_change;
            else
                heroCounter[m.hero_id] = m.rank_change;

            if (m.solo_rank)
                soloCounter += m.rank_change;
            else
                partyCounter += m.rank_change;
        }

        DateTime[] dataX = Matches.Select(m => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(m.start_time).ToLocalTime()).ToArray();
        uint[] dataY = Matches.Select(m => (uint)(m.previous_rank + m.rank_change)).ToArray();

        myPlot.Add.Scatter(dataX, dataY);
        myPlot.Axes.DateTimeTicksBottom();
        myPlot.XLabel("Time");
        myPlot.YLabel("MMR");

        File.WriteAllText(dota_cfg, mmr_history.ToString());

        Console.WriteLine("\nStatistics:\n");
        ScottPlot.WinForms.FormsPlotViewer.Launch(myPlot);

        Console.WriteLine("Double down accuracy: {0} / {1} = {2}% (estimated using matches where |rank change| > 40)", dd_won, dd_cnt, dd_cnt > 0 ? 100 * dd_won / dd_cnt : 0);
        Console.WriteLine("MMR inflated by (at least): {0}\n", mmr_inflated);

        Console.WriteLine("Total MMR gained in solo queue: {0}", soloCounter);
        Console.WriteLine("Total MMR gained in party queue: {0}", partyCounter);

        Console.Error.Write("Print detailed hero MMR stats? (y/n): ");
        String choice = Console.ReadLine();
        if (choice.Contains('y')) {
            foreach (var hero in heroCounter.OrderByDescending(x => x.Value)) {
                if (hero.Key == 0) continue;
                Console.WriteLine("{0}: {1}", heroIdToName[hero.Key], hero.Value);
            }
        }

        Console.WriteLine("Press any key to exit...");
        Console.ReadKey(true);

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

        try {
            if (File.Exists(cached_auth)) {
                var reAccessToken = File.ReadAllText(cached_auth, System.Text.Encoding.ASCII);
                /// Logon to Steam with the access token we have saved
                steamUser.LogOn(new SteamUser.LogOnDetails {
                    Username = user,
                    AccessToken = reAccessToken,
                });
            } else {
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
        } catch {
            Console.WriteLine("Error during login with username '{0}'", user);
            steamClient.Disconnect();
        }
    }

    static void OnDisconnected(SteamClient.DisconnectedCallback callback) {
        Console.WriteLine("Disconnected from Steam");
        if (!haveConnected) {
            Console.WriteLine("Trying again in 5...");
            Thread.Sleep(TimeSpan.FromSeconds(5));
            steamClient.Connect();
        } else {
            isRunning = false;
        }
    }

    static void OnLoggedOn(SteamUser.LoggedOnCallback callback) {
        if (callback.Result != EResult.OK) {
            Console.WriteLine("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);

            steamClient.Disconnect();
            return;
        }
        // save the current cellid somewhere. if we lose our saved server list, we can use this when retrieving
        // servers from the Steam Directory.
        File.WriteAllText("cellid.txt", callback.CellID.ToString());

        account = steamUser.SteamID.AccountID;

        Console.WriteLine("Logged in! Launching DOTA...");

        /// we've logged into the account
        /// now we need to inform the steam server that we're playing dota (in order to receive GC messages)

        /// steamkit doesn't expose the "play game" message through any handler, so we'll just send the message manually
        var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

        playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed {
            game_id = new GameID(APPID), /// or game_id = APPID,
		});

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
        Console.WriteLine("Logged off of Steam");
        steamClient.Disconnect();
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
        var msg = new ClientGCMsgProtobuf<CMsgDOTAGetPlayerMatchHistoryResponse>(packetMsg);
        uint partyMMR_removal = ConvertToUnixTime(new DateTime(2019, 8, 6, 0, 0, 0, DateTimeKind.Utc));

        foreach (var match in msg.Body.matches) {
            //Console.WriteLine("Match ID: {0} Start Time: {1} Rank Change: {2} Previous Rank: {3} Solo Rank: {4} Seasonal Rank: {5}",
                //match.match_id, match.start_time, match.rank_change, match.previous_rank, match.solo_rank, match.seasonal_rank);
            if (match.start_time != 0 && match.match_id != 0 && match.rank_change != 0 && match.previous_rank != 0 &&
                !(match.start_time < partyMMR_removal && !match.solo_rank)) {
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
            //steamUser.LogOff();
            steamClient.Disconnect();
        }
        else {
            Thread.Sleep(1000);
            //var start_at_match_id = msg.Body.matches[msg.Body.matches.Count - 1].match_id;
            Console.WriteLine("Matches remaining: {0} start at: {1}", matches_remaining, matches_start_at_id);
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
                            solo_rank = bool.Parse(values[3]),
                            hero_id = int.Parse(values[4]),
                            previous_rank = uint.Parse(values[5]),
                            rank_change = int.Parse(values[6]),
                        };

                        matches.Add(match);
                    }
                }
            }
        } catch (FileNotFoundException) {
        } catch (Exception e) {
            Console.WriteLine("Error reading CSV file: {0} \n Either fix the file or delete it.", e);
            System.Environment.Exit(1);
        }

        return matches;
    }

    static Dictionary<int, string> heroIdToName = new Dictionary<int, string>{
        {1, "Anti-Mage"},
        {2, "Axe"},
        {3, "Bane"},
        {4, "Bloodseeker"},
        {5, "Crystal Maiden"},
        {6, "Drow Ranger"},
        {7, "Earthshaker"},
        {8, "Juggernaut"},
        {9, "Mirana"},
        {10, "Morphling"},
        {11, "Shadow Fiend"},
        {12, "Phantom Lancer"},
        {13, "Puck"},
        {14, "Pudge"},
        {15, "Razor"},
        {16, "Sand King"},
        {17, "Storm Spirit"},
        {18, "Sven"},
        {19, "Tiny"},
        {20, "Vengeful Spirit"},
        {21, "Windranger"},
        {22, "Zeus"},
        {23, "Kunkka"},
        {25, "Lina"},
        {26, "Lion"},
        {27, "Shadow Shaman"},
        {28, "Slardar"},
        {29, "Tidehunter"},
        {30, "Witch Doctor"},
        {31, "Lich"},
        {32, "Riki"},
        {33, "Enigma"},
        {34, "Tinker"},
        {35, "Sniper"},
        {36, "Necrophos"},
        {37, "Warlock"},
        {38, "Beastmaster"},
        {39, "Queen of Pain"},
        {40, "Venomancer"},
        {41, "Faceless Void"},
        {42, "Wraith King"},
        {43, "Death Prophet"},
        {44, "Phantom Assassin"},
        {45, "Pugna"},
        {46, "Templar Assassin"},
        {47, "Viper"},
        {48, "Luna"},
        {49, "Dragon Knight"},
        {50, "Dazzle"},
        {51, "Clockwerk"},
        {52, "Leshrac"},
        {53, "Nature's Prophet"},
        {54, "Lifestealer"},
        {55, "Dark Seer"},
        {56, "Clinkz"},
        {57, "Omniknight"},
        {58, "Enchantress"},
        {59, "Huskar"},
        {60, "Night Stalker"},
        {61, "Broodmother"},
        {62, "Bounty Hunter"},
        {63, "Weaver"},
        {64, "Jakiro"},
        {65, "Batrider"},
        {66, "Chen"},
        {67, "Spectre"},
        {68, "Ancient Apparition"},
        {69, "Doom"},
        {70, "Ursa"},
        {71, "Spirit Breaker"},
        {72, "Gyrocopter"},
        {73, "Alchemist"},
        {74, "Invoker"},
        {75, "Silencer"},
        {76, "Outworld Devourer"},
        {77, "Lycan"},
        {78, "Brewmaster"},
        {79, "Shadow Demon"},
        {80, "Lone Druid"},
        {81, "Chaos Knight"},
        {82, "Meepo"},
        {83, "Treant Protector"},
        {84, "Ogre Magi"},
        {85, "Undying"},
        {86, "Rubick"},
        {87, "Disruptor"},
        {88, "Nyx Assassin"},
        {89, "Naga Siren"},
        {90, "Keeper of the Light"},
        {91, "Io"},
        {92, "Visage"},
        {93, "Slark"},
        {94, "Medusa"},
        {95, "Troll Warlord"},
        {96, "Centaur Warrunner"},
        {97, "Magnus"},
        {98, "Timbersaw"},
        {99, "Bristleback"},
        {100, "Tusk"},
        {101, "Skywrath Mage"},
        {102, "Abaddon"},
        {103, "Elder Titan"},
        {104, "Legion Commander"},
        {105, "Techies"},
        {106, "Ember Spirit"},
        {107, "Earth Spirit"},
        {108, "Underlord"},
        {109, "Terrorblade"},
        {110, "Phoenix"},
        {111, "Oracle"},
        {112, "Winter Wyvern"},
        {113, "Arc Warden"},
        {114, "Monkey King"},
        {119, "Dark Willow"},
        {120, "Pangolier"},
        {121, "Grimstroke"},
        {123, "Hoodwink"},
        {126, "Void Spirit"},
        {128, "Snapfire"},
        {129, "Mars"},
        {131, "Ring Master"},
        {135, "Dawnbreaker"},
        {136, "Marci"},
        {137, "Primal Beast"},
        {138, "Muerta"},
        {145, "Kez"},
    };
}

