# tomyum.votes: osu-server-list.com votes for TomYum

A loose-source TomYum plugin. When someone votes for the server on
[osu-server-list.com](https://osu-server-list.com/server/somtum/vote) (OSL), OSL calls the server and the
player gets their vote counted plus days of osu!direct. It works like bancho.py's `/vote-callback`.

## What it does

- **`GET /vote-callback?username=<name>&key=<key>`**, the URL OSL calls. It answers the same way bancho.py did:
  - `401 Invalid API key.` when the key is wrong or missing, or when `apiKey` isn't set. The key is compared in constant time.
  - `404 User not found.` when there's no such player (deleted accounts count as not found). Names are matched like
    osu-web matches them: any case, and spaces and underscores are treated as the same.
  - `200` with an empty body when the vote is counted.
  - `503` when the plugin has no database access, and `500` when the database fails (the error is logged).
- **Reward: osu!direct, not supporter.** Each vote adds `daysPerVote` days of osu!direct (`direct_until` in the
  plugin's table), on top of any time the player still has, or from now if it has run out. Nothing else of
  supporter's comes with it (unlimited name changes, profile perks, upload quota...); supporter stays what the
  website's store sells, and votes don't touch it.
  - **In game:** at an osu!stable login TomYum raises `ClientPerksEvent`; the plugin sets `OsuDirect` for a voter
    with time left, and TomYum sends that session the client's supporter bit (the client only shows osu!direct
    with it). New time takes effect on the player's next login.
  - **On the website:** osu-web reads `direct_until` too (`TomYumVotes`) and shows its osu!direct button.
- **BanchoBot thanks the voter** if they're online in osu!stable: "Thank you for voting for somtum.fun! ...", then
  how many days of osu!direct they got and the new end date.
- **`!vote`** shows the player's vote count, when their osu!direct (or supporter) ends, and the voting link.
- **Bought supporter that has run out is taken off.** Nothing else in this stack does this: osu-web's store only
  sets the end date, and TomYum only reads `osu_subscriber`. Every `expireIntervalMinutes`, the plugin sets
  `osu_subscriber = 0` for everyone whose end date has passed. It keeps the end date, so the website still shows
  that they've supported. Players who are online get a notification. Supporter with no end date is never touched.
  (Before 1.1.0 votes gave supporter time; that time runs out the same way.)

### Where the data is

- **Vote counts** go in the `plugin_votes` table in **TomYum's own database** (`Database:Name`, which is
  `tomyum` in production). That's the default database of the plugin's trusted connection. The table is created
  the first time it's needed:
  `user_id` (primary key), `votes`, `first_voted_at`, `last_voted_at`, `direct_until` (UTC; added to older tables).
- **Supporter** is in the website's database (`osu`, see `osuWebDatabase`), in `phpbb_users` (only read, and ended).

## Config (`Plugins:tomyum.votes`)

| Key | Default | |
|---|---|---|
| `apiKey` | *(empty)* | OSL's API key (bancho.py's `OSU_SERVER_LIST_API_KEY`). **Required.** While it's empty, every callback gets 401. |
| `voteUrl` | `https://osu-server-list.com/server/somtum/vote` | The link `!vote` shows. |
| `daysPerVote` | `1` | Days of osu!direct per vote. `0` counts votes without giving anything. |
| `subdomains` | `osu,c` | The host names (first label) the callback answers on. `*` means every host TomYum serves. |
| `osuWebDatabase` | `osu` | The website's database name. |
| `expireSupporter` | `true` | Take ended supporter off. Leave it on unless something else does this. |
| `expireIntervalMinutes` | `5` | How often ended supporter is checked. |

The plugin also has to be in **`Plugins:Trusted`**. Without that it gets no database access and can't count votes.

As environment variables, TomYum reads `TOMYUM_` + the key with `__` for `:`. The plugin id keeps its dot:

```
TOMYUM_PLUGINS__TRUSTED__1=tomyum.votes          # __0 is tomyum.metrics on this server
TOMYUM_PLUGINS__tomyum.votes__apiKey=<OSL key>
TOMYUM_PLUGINS__tomyum.votes__daysPerVote=1      # optional
```

Keys aren't case-sensitive. `tests/.../ConfigBindingTests.cs` checks these names against .NET's configuration
binding, the same way TomYum reads them.

## Deploying (blueskychan.dev)

1. Put this repo on the server, for example at `~/somtum-dev/tomyum-votes-plugins`.
2. In `TomYum/deploy/production/docker-compose.override.yml`, under `server:`, mount **only the plugin folder**
   (not the repo, or the loader would compile the tests too) and add the settings:

   ```yaml
   server:
     volumes:
       - ../../plugins/tomyum-metrics:/app/plugins/tomyum-metrics:ro
       - ../../../tomyum-votes-plugins/tomyum-votes:/app/plugins/tomyum-votes:ro
     environment:
       TOMYUM_PLUGINS__TRUSTED__0: tomyum.metrics
       TOMYUM_PLUGINS__TRUSTED__1: tomyum.votes
       TOMYUM_PLUGINS__tomyum.votes__apiKey: ${OSL_API_KEY:-}
   ```

   Then put `OSL_API_KEY=<key>` in `deploy/production/.env`.
3. Route the callback to TomYum on `osu.` (only if OSL should call `osu.<domain>`, like bancho.py was called).
   The stack's Caddy sends `osu.` to the website by default, so add `/vote-callback` to the `@tomyum` paths in
   `deploy/production/Caddyfile`:

   ```
   @tomyum path /web/* /d/* /beatmap-submission/* /ss/* /difficulty-rating /release/* /rating/* /vote-callback
   ```

   `c.<domain>` already goes to TomYum, so it needs no change.
4. Restart: `sudo docker compose up -d server` (and `sudo docker compose restart caddy` after a Caddyfile change).
5. Check the log: `plugin loaded: tomyum.votes v1.0.0 ...` and `tomyum.votes: GET /vote-callback on osu.<domain>, c.<domain>; ...`.
6. In OSL's server settings, set the callback URL. bancho.py appended `?username=...&key=...` to:
   - `https://c.blueskychan.dev/vote-callback` (works without the Caddy change, and `c.` isn't in Caddy's access
     log, so the key isn't written to disk), or
   - `https://osu.blueskychan.dev/vote-callback` (needs step 3; `osu.` requests, query string included, are written
     to Caddy's access log).

Quick check (the key is wrong on purpose, so nothing is changed):

```sh
curl -s -w ' %{http_code}\n' 'https://c.blueskychan.dev/vote-callback?username=test&key=wrong'   # Invalid API key. 401
```

## Building and tests

The plugin is the `tomyum-votes/` folder: `plugin.json` and `.cs` files that the server compiles with Roslyn
when it starts. `tests/TomYum.Votes.Tests` compiles the same files against `TomYum.Plugins.Abstractions` from the
TomYum checkout next to this repo (`../TomYum`):

```sh
dotnet build -c Release     # 0 warnings; analyzers on, warnings are errors
dotnet test -c Release
```

The tests cover:

- the callback's answers, the key check, rewards, BanchoBot messages, `!vote`, and the expiry pass, using fakes;
- the folder compiling the way TomYum's `PluginLoader` compiles it (no implicit usings, nullable on);
- the config names.

The SQL tests run only when `TOMYUM_VOTES_TEST_MYSQL` holds a connection string to a **throwaway** MySQL server.
Each test creates and drops its own databases. For example:

```sh
docker run -d --rm --name votes-test-mysql -e MYSQL_ROOT_PASSWORD=test -p 127.0.0.1:33999:3306 mysql:8.4
TOMYUM_VOTES_TEST_MYSQL="Server=127.0.0.1;Port=33999;User ID=root;Password=test" dotnet test -c Release
docker stop votes-test-mysql
```

Never point it at the production database.
