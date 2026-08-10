# Steam Setup and Smoke Test

`480` (Spacewar) is used only to develop the integration. Do not ship with it.

1. Create the real Steam app in Steamworks and provide its ID with `-steamAppId <id>`, `PUSHUP_STEAM_APP_ID`, Steam's launch environment, or an adjacent `steam_appid.txt`.
2. Do not commit `steam_appid.txt`. A non-development build fails safely when its ID is missing, and rejects `480` unless explicitly configured as a private playtest.
3. For a private Windows/Linux friend test, run `PushUp.Editor.PushUpDevelopmentBuild.BuildSteamPlaytestAll`. The generated folders include `steam_appid.txt` with `480`; this exception is only for an unpacked private playtest and not a Steam depot.
4. Launch each player from a separate Steam account. The host chooses **Host Steam Friends Game**, waits for the lobby screen, invites friends, and presses **Start Hill**. Creating the lobby does not spawn or simulate the level.
5. A client may accept the in-game invite, Steam's external Join action, or choose **Join Friend Game**. Before the host starts, the client remains on the lobby screen. After start it progresses through Connecting, Authenticating, and Waiting for Player, then enters automatically.
6. Verify lobby metadata rejection (protocol/build mismatch), full-lobby rejection, cold join, late join at base camp, host departure, cancel/retry, and relay connectivity.
7. Test around 100 ms RTT and 2% packet loss. Local player input should remain responsive; remote roots/boulder should interpolate without repeated large corrections.

The Steam host runs FishNet's authoritative server and plays directly in that process. Only the three friend clients open Steam P2P connections, yielding four total players without connecting the host to its own Steam ID.

Before a Steam depot build, configure the real app ID in the Steam launch environment/CI and remove private-playtest launch flags and `steam_appid.txt` from the depot.
