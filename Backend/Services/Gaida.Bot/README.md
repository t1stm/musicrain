# Gaida.Bot

A Discord bot that plays what the running Gaida instance serves. It is an HTTP client of the API
like the frontend is: it holds no library, runs no ffmpeg, and caches nothing.

Audio goes out as Opus and is never decoded on the way. `/Audio/Download/Opus/{bitrate}` hands out
an Ogg/Opus stream, `AudioFormat.Opus` takes one, and the bytes pass through untouched.

## Configuration

`.env.json` beside the binary, or the whole array inline in `BOT_CONFIGURATION`. `CONFIGURATION_LOCATION`
points at a different file. [`.env.example.json`](.env.example.json) is the shape — copy it:

```bash
cp .env.example.json .env.json
```

`master` marks the account that listens for commands. The others only join voice channels the master
cannot serve, because it is already playing somewhere else in that guild — each one owning the
statusbar of the player it is running, which spreads the message-edit rate limits over several
applications. Prefixes on a non-master account are ignored: it would answer every command twice.
With no account marked, the first one is the master.

| Variable | Default | What it does |
| --- | --- | --- |
| `GAIDA_API_BASE_URL` | `http://localhost:5340` | The running instance |
| `GAIDA_OPUS_BITRATE` | the voice channel's own bitrate | Encode bitrate in kbps, 8–256 |
| `GAIDA_STATUSBAR_INTERVAL_MS` | `3200` | How often the player message is edited |
| `STATUS_LOCATION` | `.status.json` | Where the Discord status set in Oko is remembered |

## In compose

The bot is behind the `bot` profile, so the default `docker compose up` leaves it out — it needs a
token to be worth starting.

The `.env.json` above is mounted read-only at `/config/.env.json`, so the accounts are configured
once and `dotnet run` and compose read the same file. Point `BOT_CONFIG_FILE` elsewhere to mount a
different one. A deployment that would rather keep its secrets the way the rest of the stack does
can put the whole array on one line in `.env` beside `compose.yaml` instead, which overrides the
file:

```
BOT_CONFIGURATION=[{"name":"gaida","token":"…","prefixes":["-","="],"master":true}]
```

```bash
docker compose --profile bot up -d --build gaida-bot
docker compose logs -f gaida-bot
```

Inside the network it reaches the API as `http://gaida-api:8080`, publishes no port, and needs no
ffmpeg — the audio is never decoded. Two native details do need care in a container, because
DSharpPlus's current `libopus.so` is built against a glibc newer than any base image has: see the
`DSharpPlus.Natives.Opus` pin in [Gaida.Bot.csproj](Gaida.Bot.csproj) and the comment in the
[Dockerfile](Dockerfile). The file is never
copied into the image, so no token is baked into one. With neither the file nor
`BOT_CONFIGURATION`, the bot logs what is missing and exits rather than crash-looping.

## Commands

Prefix or slash. Aliases are prefix-only.

| Command | Aliases | Does |
| --- | --- | --- |
| `play <search>` | `p` `плаъ` `п` `udri` `удри` | Queues what the search finds — text, a link, or a playlist — and joins if it is not already playing |
| `playselect <search>` | `ps` | The same, after picking from a dropdown of the first 25 results |
| `playnext <search>` | `pn` `плаън` `пн` | Queues it after the current track. A bare number moves that queue entry instead |
| `skip [times]` | `next` `скип` `неьт` | Forwards |
| `previous [times]` | `back` `prev` `бацк` `прев` `прежиоус` | Backwards |
| `pause` | `паусе` | Toggles |
| `leave` | `l` `stop` `леаже` `л` `стоп` `с` `s` `die` `дие` | Says goodbye and disconnects |
| `loop` | `лооп` | None → whole queue → one track → none |
| `shuffle [seed]` | `rand` `схуффле` `ранд` | Shuffles, keeping the current track first. A number shuffles reproducibly |
| `remove <index or name>` | `r` `rm` `реможе` `рм` `р` | Drops one entry |
| `move <a b>` or `<name !to name>` | `m` `mv` `м` `може` `мж` | Moves one entry, or swaps two |
| `goto <index>` | `гото` `go` `го` `skipto` `скипто` | Jumps |
| `list` | `queue` `лист` `яуеуе` | Sends the queue as `queue.txt` |
| `clear` | | Empties the queue but keeps what is playing |
| `lyrics [search]` | | The words for the current track, or for what the search finds |

The player message carries five buttons — Shuffle, Previous, Play / Pause, Next, Leave — and only
answers someone who is in that player's voice channel.

## Reporting to Oko

The bot answers the same `/Admin` surface every other service here does — `/Admin/snapshot`,
`/Admin/requests` and `/Admin/events` on port 8080, behind `ADMIN_TOKEN`, 404 without it — so Oko
watches it like any other target. It is the one thing the bot listens for; everything else it does
is outbound.

The snapshot carries the connected accounts (which one is master, how many guilds each is in, the
status an operator set it to), every live player (guild, channel, listeners, what is playing, queue
position, loop, elapsed) and the audit trail. `/Admin/requests` stays empty: nothing makes HTTP
requests to the bot.

Two routes act rather than report, and they are the only things in the stack that change what the
bot is doing from outside Discord:

| Route | Parameters |
| --- | --- |
| `POST /Admin/set-status` | `account`, `presence` (`Online`, `Idle`, `DoNotDisturb`, `Invisible`), `activity` (`none`, `Playing`, `ListeningTo`, `Watching`, `Competing`, `Streaming`), `text`, `url` for streaming |
| `POST /Admin/clear-status` | `account` |

`account` is the configured name — `name#index` where two accounts share one, or have none. The
status is applied to the live connection and written to `STATUS_LOCATION`, so a restart brings the
account back wearing it: it rides the gateway IDENTIFY rather than being set after connecting.
Discord's own rules are enforced here rather than in Oko — an activity needs text, text stops at 128
characters, `Streaming` needs a twitch.tv or youtube.com URL, and `Custom` is refused because
DSharpPlus exposes no way to set the `state` field it is read from.

The audit trail is the bot's own, because Oko's audit log records what an operator changed through
the panel and nothing a watched service can write into. It keeps the last 500 of:

| Kind | When |
| --- | --- |
| `connected` | an account came up, and whether it is the master |
| `join` | an account took a voice channel, and whether another was already playing in that guild |
| `refused` | somebody asked and every account was busy in that guild |
| `move` | the bot was dragged to another channel, from where to where |
| `kicked` | it was disconnected from voice by someone else |
| `lost` | the voice connection was lost for good, with the reason the library gave |
| `leave` | it left, and whether that was the fifteen-minute empty-queue timeout |
| `track` | a track started, and who queued it |
| `command` / `command-failed` | who ran which command, where |
| `button` | who pressed which player button |
| `status` | an operator set or cleared this account's Discord status |

In memory, capped, gone on restart — like Oko's own log and the request ring in `Gaida.Admin`.

## How the audio is paced

DSharpPlus's send queue is unbounded and cannot be cleared, so anything written to it is committed
to being played. The bot therefore feeds the stream in real time, reading the granule positions out
of the Ogg pages to know exactly how much audio it has handed over, and staying one second ahead of
playback. That one second is also the tail still heard after a skip or a pause.

Track changes are gapless: the next track's body is opened five seconds before the current one ends
and starts feeding the moment it finishes, so the queue never empties and the connection never falls
silent. Silence is signalled only on a pause, an empty queue, and a stop.

There is no seeking and no HTTP `Range` request. Dragging the bot to another channel does not
interrupt playback: DSharpPlus.Voice moves the live connection, and the bot only notes where it now
is. A connection that is lost for good ends the player, once the library has given up retrying.

## Tests

The pure parts — the Ogg granule scanner, the queue's index arithmetic, the progress bar — are
covered in `Tests/Gaida.Tests/BotPlaybackTests.cs`:

```bash
dotnet test Tests/Gaida.Tests --filter FullyQualifiedName~BotPlaybackTests
```
