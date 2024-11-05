# ShowMMR

Uses the GameCoordinator API to fetch and save an account's match history and visualize its MMR progression.
![image](https://github.com/user-attachments/assets/35d7f33c-f2b5-4e4b-b355-fc2e670eee9c)

## Usage

Extract [the zip](https://github.com/Lypheo/ShowMMR/releases/latest/download/ShowMMR.zip) contents into a folder. **Make sure Dota 2 is not running** (Valve allows only one concurrent GameCoordinator connection per account). Start ``ShowMMR.exe``, either by double-clicking ShowMMR.exe or alternatively via the CLI (type "cmd" into explorer adress bar, type ".\ShowMMR.exe" and press enter).
Login with your credentials and choose the number of fetches to fetch. The tool will fetch your match history at a pace of 1 page (20 matches) per second.
Once it’s done, it will save the retrieved history to disk as a .csv file (keys: ``Date,Unix time,MatchID,Start MMR,Rank Change``) and plot an interactive MMR graph.

After the first run, the tool will reuse the .csv file to avoid re-fetching matches. New recent matches will automatically be appended to the list.
If you pass 0 for "number of matches to fetch", no new matches will be fetched and the last known MMR graph will be shown.

#### Rate limit

The API endpoint used to fetch matches has a rate-limit of about 500 requests per day, so if you fetch more than 10k matches, the tool might crash and you won’t be able to load match histories in the client for a while.

If you have more than 10k matches, just wait a day after the first run and then fetch the remaining matches (they will be prepended to the saved list).

### How

This tool essentially queries the same GameCoordinator API endpoint as the Battle Stats tab. Accordingly, the received match data includes rank change information, which can be used to reconstruct the mmr history.
(Like the Battle Stats, this data can only be requested for your own account, which is why this tool requires a login.)

If you’re wondering why sites like opendota or stratz can’t do this, it’s because they only use the official Steam APIs,
not the GameCoordinator API, which is undocumented, rate-limited and can only be accessed while programmatically simulating a Dota2 session.

## Credit

Based on [AveYo’s tool](https://github.com/AveYo/ShowMMR/tree/main/ShowMMR_tool).
