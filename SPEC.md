# SPEC

Zorin Connect W10M. Port of Zorin Connect Android 1.33.4 (KDE Connect protocol v8, pkg com.zorinos.zorin_connect vc13304). Android source ref = `/mnt/ssd-raid/vm-shared/com.zorinos.zorin_connect_13304_src.tar.gz/` (dir, not tarball). Android paths below relative to its `src/org/kde/kdeconnect/`.

## §G GOAL

Full UWP ARM32 app for W10M (640XL/1520): pairs + interops w/ Zorin Connect desktop (GSConnect fork) & kdeconnect-kde, feature-parity w/ Android app via UWP equivalents. Same wire protocol v8, same plugins where platform allows, documented tier for platform-blocked ones.

## §C CONSTRAINTS

- C# UWP + XAML. TargetPlatformVersion **10.0.16299.0**, TargetPlatformMinVersion **10.0.15063.0**. ARM32. .NET Native release. Package = **.appx always** (⊥ msix, ⊥ appxbundle).
- **Dependency lockstep w/ confirmed-working W10M projects** (Jellyfin.W10M, PocketTavern.UWP, Seeneva.UWP, Baconit @ /mnt/ssd-raid/vm-shared/): csproj ToolsVersion 15.0, `<RestoreProjectStyle>PackageReference</RestoreProjectStyle>`, **Microsoft.NETCore.UniversalWindowsPlatform 6.1.9** (⊥ 6.2.x), **Newtonsoft.Json 13.0.3** (= packet JSON lib). ⊥ new deps beyond baseline except BC below.
- Crypto/TLS = **BouncyCastle C#**: cert gen + TLS 1.2 client AND server over StreamSocket streams. ONLY dep added vs baseline — nothing in baseline does TLS server. Prefer Portable.BouncyCastle (netstandard2.0 asset — same resolution path as Newtonsoft 13.0.3 under UWP 6.1.9, proven); fallback legacy BouncyCastle 1.8.1 (PCL asset) if netstandard2.0 refuses at min 15063 — settled at first device deploy (T4). ⊥ SslStream (server mode + client-cert unreliable on .NET Native), ⊥ StreamSocket.UpgradeToSslAsync (client-only, no pin control, no server role).
- Sockets = Windows.Networking.Sockets (StreamSocket/StreamSocketListener/DatagramSocket). mDNS = Windows.Networking.ServiceDiscovery.Dnssd.
- Build on VM (`ssh -p 2222 Starkka15@localhost`, Z:=vm-shared), VS2022, SDK 16299. Foreground builds. Deploy via WDP (see revenant-wdp-pairing; 640XL portal 192.168.5.17:443).
- Wire compat non-negotiable: desktop peer unmodified. Test peers: Zorin Connect GNOME ext + kdeconnect-kde.
- ⊥ stubs shipped: plugin not implemented → its packet types ∉ identity capabilities. Partial plugin → only implemented directions advertised.
- Release scoreboard = pairs + plugin works against real desktop from phone. ⊥ green-build chasing.
- Restricted-cap features (rescap) = tiered: probe on device first, then full impl. ⊥ silent drop — blocked = documented in §T w/ reason.
- MprisReceiver (desktop controls phone's media apps) **⊥ portable**: W10M has no cross-app media-session enumeration (MediaSessionManager analog absent; SMTC = own app only). Excluded from capabilities. Only known platform-impossible plugin.

## §I INTERFACES

Wire (all must match Android impl exactly):
- udp: port 1716 broadcast + unicast(custom hosts). Identity packet + extra field `tcpPort`. Listener binds 1716 reuseaddr; bind fail non-fatal (send still works).
- tcp: server first free port 1716..1764, advertised in `tcpPort`. UDP-receiver connects out → sends own identity plaintext (1 line) → then TLS. Accept side reads 1 plaintext identity line (cap 512KiB).
- tls: TLS 1.2. Role rule: TCP-connector = TLS **server**; TCP-accepter (= UDP broadcaster) = TLS client. Paired → pin stored peer cert, server side needClientAuth; unpaired → trust-all + wantClientAuth. Post-handshake (v8): both re-send identity encrypted; encrypted identity authoritative, plaintext bootstrap only. Peer cert captured from handshake. 10s socket timeout during handshake/reads (read timeout → retry loop, not disconnect).
- payload: sender opens ServerSocket first free 1739..1764, packet gets `payloadSize` + `payloadTransferInfo:{port}`, receiver connects to main-link remote addr:port. Payload socket TLS, same role rule (payload sender = TLS server). accept timeout 10s. copy buf 4096, progress cb ≥500ms apart.
- mdns: announce+browse `_kdeconnect._udp`, instance name = deviceId, port 1716, TXT id/name/type/protocol. On resolve → send unicast UDP identity to host (⊥ direct TCP connect).
- packet: single-line JSON `{"id":<ms>,"type":"kdeconnect.X","body":{...}}` + `\n`, UTF-8, `\/`→`/` unescape (QJson). id=timestamp, not correlated. Identity/UDP size cap 512KiB. Regular packets uncapped.
- identity body: deviceId, deviceName, protocolVersion=8, deviceType="phone", incomingCapabilities[], outgoingCapabilities[].
- pairing: `kdeconnect.pair` {pair:bool, timestamp:<unix-sec, v8 request only>}. Accept reply {pair:true} no timestamp. See §V7-V9.
- rate limits: ≥1000ms between connections per deviceId AND per source IP (maps pruned >255 entries); ≥200ms between UDP broadcasts.
- deviceId: 32-hex (UUID no dashes), regex `^[a-zA-Z0-9_-]{32,38}$` on inbound. deviceName filter: strip `"',;:.!?()[]<>`, trim, max 32.
- cert: X.509v3 self-signed, CN=<deviceId>, OU=KDE Connect, O=KDE, serial 1, notBefore=now−1y, notAfter=now+10y. Key EC secp256r1, sig SHA512withECDSA.
- downgrade guard: trusted device advertises protocolVersion < stored → refuse connection. Store protocolVersion per device.

App surfaces:
- Share Target contract: DataTransferManager share-target activation, `*/*` files + text + url → SharePlugin send.
- Protocol activation: `zorinconnect://runcommand[/<deviceId>]` → RunCommand page.
- appx caps: internetClient, internetClientServer, privateNetworkClientServer, contacts, userNotificationListener, picturesLibrary, musicLibrary, videosLibrary, videosLibrary, removableStorage, backgroundMediaPlayback. Tier-R (rescap): chat, smsSend, phoneCall, phoneCallHistory, inputInjectionBrokered.
- storage: ApplicationData.LocalSettings containers: `app` (deviceId, deviceName, privateKey b64, certificate b64), `trusted_devices` (deviceId→bool), per-device container `<deviceId>` (certificate, deviceName, deviceType, protocolVersion, <pluginKey> enable bools, plugin-private). Blobs >4KB → LocalFolder files. Mirrors Android SharedPreferences layout 1:1.

## §V INVARIANTS

V1: ∀ feature → verified against unmodified desktop peer (GSConnect + kdeconnect-kde) from phone. Build green ≠ done.
V2: TLS role exact: main link — TCP *connector* = TLS server, TCP accepter = TLS client. Payload link — payload *sender* (ServerSocket owner, i.e. accepter) = TLS server, receiver = TLS client. Note roles flip between main/payload relative to accept side. Deviation → desktop handshake fail.
V3: paired device → peer cert ! byte-equal stored cert; mismatch → drop link, ⊥ silent re-trust. Unpaired → accept any cert (TOFU at pair time).
V4: v8 → encrypted identity authoritative; plaintext identity used only for deviceId/tcpPort bootstrap. v<8 peer → plaintext identity used as-is (compat kept).
V5: inbound identity: deviceId regex + filtered non-blank name, else drop. UDP/identity packets >512KiB dropped.
V6: rate limits enforced (1000ms/deviceId, 1000ms/IP, 200ms broadcast) AND tolerated inbound (own reconnects spaced ≥1s).
V7: pair request out ! include timestamp (unix sec). Inbound v8 pair request w/o timestamp → reject. |their_ts − now| > 1800s → fail "clocks not match".
V8: verify key = SHA-256( concat(pubkeyDER_larger, pubkeyDER_smaller by unsigned lexicographic — LARGER FIRST) + ASCII-decimal(timestamp) ), display first 8 hex UPPER. v7 peer: same w/o timestamp. Shown during Requested/RequestedByPeer only.
V9: pair accept reply = {pair:true} w/o timestamp. Request timeout 30s out, 25s inbound-display. pair:false in Paired → unpair.
V10: capabilities advertised = exactly implemented packet types. ∀ plugin ∉ build → type ∉ identity. Desktop must never see dead capability.
V11: cert/key regenerated (corrupt/expired/CN≠deviceId) → wipe trusted_devices + all per-device containers.
V12: packet serialize: single line + `\n`, `\/` unescaped, UTF-8. Read loop = line-based, empty lines skipped, null=EOF.
V13: ∀ release milestone → .NET Native ARM release build tested on 640XL (release-only failures: reflection/rd.xml — Android R8 lesson).
V14: ∀ API call >15063 baseline → ApiInformation guard. Manifest min 10.0.15063.0 exact (confirmed-install rule; ⊥ 15254, ⊥ 16299). Deps ! match §C lockstep versions.
V15: per-device send queue: single writer/device, unbounded, drained in order; send fail → disconnect link. Socket death → 300ms grace for replacement link before reachable=false.
V16: payload transfer: received bytes ≠ payloadSize → delete partial file + error. Multi-file batch: queue idle >1s before numberOfFiles reached → batch failed.
V17: SFTP creds: user="kdeconnect", password 28-char random regenerated per start, password compare constant-time; pubkey auth accepts only paired device cert pubkey. Port 1739..1764 first free.
V18: unpair → remove trusted_devices entry + clear whole per-device container + notify plugins (incl. not-loaded ones).
V19: trusted networks: untrusted SSID → ⊥ broadcast, ⊥ mDNS announce, inbound identity from non-paired ignored; paired devices always allowed. Default all-allowed.
V20: protocolVersion + capabilities ⊥ persisted across app restarts (re-learned each connection); protocolVersion persisted only for downgrade guard.
V21: RETRACTED (was "UDP send fatal" — misdiagnosis, see §B1). UDP send/broadcast works.
V22: ⊥ `JToken.Value<T>()` / `JToken.Values<T>()` anywhere — generic reflection fast-fails under .NET Native ARM (uncatchable, no dump). Use explicit `(long)tok` / `(string)tok` / `tok.ToString()` casts gated on `tok.Type` (JTokenType checks). Applies to ALL packet body reads across every plugin. StartupTrace (LocalSettings-sync + MusicLibrary\zctrace.txt, readable via WDP `/api/filesystem/apps/file?knownfolderid=Music&filename=zctrace.txt`) + Isolate* toggles STAY until release — the diagnostic that cracked this.

## §T TASKS

id|status|task|cites
T1|x|scaffold: UWP C# sln, ARM, 16299/min15063, csproj cloned from PocketTavern.UWP pattern (ToolsVersion 15, PackageReference, UWP 6.1.9, Newtonsoft 13.0.3), appx self-sign, VM build script Z:\zorinconnect\build.bat, WDP deploy script (clone revenant_deploy.py pattern)|C
T3|~|NetworkPacket: JSON model, serialize/deserialize per §I.packet, caps, payload fields|V12,V5
T4|~|identity/settings core: deviceId gen+store, name (default = device model, filter), EC keypair + self-signed cert via BC (settles Portable.BouncyCastle vs 1.8.1 PCL fallback on first device deploy), LocalSettings layout §I.storage|V11
T5|~|UDP discovery: DatagramSocket listener 1716 + broadcaster (bcast + custom hosts unicast), identity+tcpPort, rate limits|V5,V6
T6|.|TCP: StreamSocketListener 1716..1764 + outbound connect on UDP identity recv, plaintext identity bootstrap both directions|V4
T7|.|BC TLS wrapper: TlsServerProtocol/TlsClientProtocol over StreamSocket streams, role rule, pin/trust-all modes, client-cert req, peer-cert capture, 10s timeouts, v8 encrypted identity re-exchange|V2,V3,V4
T8|.|Device+Link layer: device registry (ConcurrentDict), links priority list, reachable/paired axes, send queue, read loop, packet dispatch to plugins, DeviceStats(24h mem)|V15,V20
T9|x|PairingHandler: v8 state machine, timestamps, verify-key, timeouts, accept/reject, TOFU persist, unpair|V7,V8,V9,V18
T10|x|plugin framework: IPlugin (onCreate/onDestroy/onPacket, supported/outgoing types, enabledByDefault, per-device settings, req/opt permission gates), factory registry, capability intersection loading|V10
T11|x|payload channel: sender ServerSocket 1739..1764 + TLS, receiver connect, progress, cancel|V2,V16
T12|.|mDNS: Dnssd announce (instance=deviceId) + DeviceWatcher browse → unicast identity reply|V19
T13|.|UI shell: MainPage (hamburger: device list Connected/Available/Remembered, refresh), DevicePage (pair btn/verify code/accept-reject, plugin grid, unpair, encryption-info dialog: both cert SHA256 fingerprints + protocol ver), SettingsPage (device name, theme, trusted networks, custom hosts list w/ validation `^[0-9A-Za-z._-]+$`), PluginSettingsPage per device|-
T14|.|trusted networks: SSID via WlanConnectionProfileDetails.GetConnectedSsid, list UI, enforcement|V19
T15|x|plugin Ping: both directions, toast on recv, context-menu send|V1
T16|x|plugin Battery: PowerManager.RemainingChargePercent/BatteryStatus → kdeconnect.battery {currentCharge,isCharging,thresholdEvent 0/1 low@≤15 once-latch !charging}, delta-only sends, recv → device list UI|V1
T17|x|plugin FindMyPhone: recv request → navigate FindMyPhone page + loop ringtone max vol (MediaPlayer, restore vol on stop) + vibrate (VibrationDevice); FindRemoteDevice: send request from context menu|V1
T18|x|plugin Clipboard: DataTransfer.Clipboard, ContentChanged(foreground) → kdeconnect.clipboard{content}; clipboard.connect{timestamp ms,content} on link up; recv → SetContent; conflict: connect applied iff ts≠0 & ts≥local ts; resync on app resume + manual send button|V1
T19|x|plugin Share: recv file→DownloadsFolder.CreateFileAsync (+lastModified apply, open flag→Launcher), text→clipboard+toast, url→LaunchUriAsync; send via ShareTarget contract + picker; numberOfFiles/totalPayloadSize + .update packet; batch semantics|V16,V1
T20|.|plugin RunCommand: request list on create, cache per device, list page tap→{key}, setup packet, zorinconnect://runcommand activation, pin-to-start secondary tiles per command|V1
T21|.|plugin MousePad (send): touchpad page (deltas ×accel×sens, 2-finger scroll, 1/2/3-tap L/R/M, hold=drag, gyro mode Gyrometer), keyboard via CharacterReceived/KeyDown → key/specialKey/modifiers, recv keyboardstate gate; SpecialKeysMap 1-32 exact|V1
T22|.|plugin Presenter: gyro pointer {dx,dy}=−gyro×0.04, stop packet, prev/next=PgUp/PgDn specialKey, fullscreen/esc|V1
T23|.|plugin SystemVolume: recv sinkList/deltas, sliders+mute+default in DevicePage, send request packets|V1
T24|.|plugin Mpris (control desktop): player map, requestPlayerList/NowPlaying/Volume handshake, now-playing page (play/pause/seek pos-extrapolation/volume/loop/shuffle/player tabs), album art via payload+disk cache (url schemes http/https/file/kdeconnect only)|V1
T25|x|plugin ConnectivityReport: ConnectionProfile.GetSignalBars(0-5→0-4) + WwanDataClass→networkType string map, send on NetworkStatusChanged, subscriptionId="0"|V1
T26|.|plugin Contacts: ContactStore AllContactsReadOnly, vCard 2.1 build + X-KDECONNECT-ID-DEV-<deviceId>:<uid> + X-KDECONNECT-TIMESTAMP lines, uid=Contact.Id, timestamp=FNV hash of vCard body (stable-until-change; UWP lacks lastModified), request_all_uids_timestamps + request_vcards_by_uid responders, per-device consent dialog|V1
T27|.|plugin ReceiveNotifications: default OFF, send request{request:true} on create, recv kdeconnect.notification (req ticker/appName/id, skip silent) → toast, icon payload→LocalCache png→toast image, tag kdeconnectId:<id>|V1
T28|x|plugin Notifications (mirror phone→desktop): UserNotificationListener (cap userNotificationListener, RequestAccessAsync), enumerate+UserNotificationChangedTrigger, map: id=UserNotification.Id, appName=AppInfo, title/text from toast bindings, time, isClearable=true, icon=AppDisplayInfo logo→PNG payload+MD5 payloadHash; recv request{request:true}→replay silent:true, cancel→RemoveNotification; actions/replies ⊥ (UWP can't invoke foreign toast actions) → no actions[]/requestReplyId ever sent|V10,V1
T29|~|background: ExtendedExecutionSession (minimized keep-alive) + suspend→persist state/resume→refresh; toast-based pairing accept/reject while backgrounded|V1
T30|.|background phase2: SocketActivityTrigger — transfer main StreamSocket to broker on suspend, in-proc OnBackgroundActivated re-handshake TLS on wake, ControlChannelTrigger fallback eval. Measure battery|V1
T31|x|probe Tier-R caps on 15254 sideload: chat+smsSend (ChatMessageStore), phoneCall (PhoneCallManager.CallStateChanged), phoneCallHistory, inputInjectionBrokered (InputInjector.TryCreate). Each: declare rescap, deploy, call API, record works/denied → gates T32-T35|V14
T32|x|plugin SMS (if T31 chat OK): ChatMessageStore reader → sms.messages v2 mapping (thread_id=ChatConversation.Id, addresses, date, type in/sent, read, sub_id=0), request_conversations→newest per thread, request_conversation ranged, ChangeTracker→live updates after first request, send via ChatMessageManager (attachments→ChatMessageAttachment, request_attachment→payload)|V1
T33|.|plugin Telephony (if T31 phone OK): CallStateChanged → ringing/talking/missedCall events, number+contact via PhoneCallHistoryStore post-call, isCancel resend on idle, request_mute → no-op documented (no ringer API)|V10
T34|x|plugin MouseReceiver (if T31 inject OK): recv mousepad.request → InputInjector mouse moves/clicks/scroll, cursor overlay n/a → direct inject; else plugin excluded|V10
T35|x|plugin RemoteKeyboard (if T31 inject OK): key/specialKey→InjectedInputKeyboardInfo incl. modifiers, echo w/ isAck when sendAck, keyboardstate true iff injector live; else excluded|V10
T36|.|plugin SFTP: C# SSH2 server (BC crypto; transport+auth+connection layers) + SFTPv3 subsystem, port 1739..1764, hostkey=device EC key, auth per §V17, roots=Pictures/Music/Videos/Documents/Removable → multiPaths+pathNames, StorageFolder-backed FS (read/write/rename/delete/list), startBrowsing→kdeconnect.sftp reply, errorMessage when no access|V17,V1
T37|.|plugin Bigscreen: TV-peer gated page, dpad/select/home specialKeys, stt via SpeechRecognizer→bigscreen.stt|V1
T38|.|Mpris lockscreen: SMTC integration (BackgroundMediaPlayer silent session) → play/pause/next/prev from lockscreen control desktop player|V1
T39|.|hardening: 1520 + 640XL soak, reconnect storms, wifi drop/rejoin, cert regen path, memory (2GB/1GB tiers), release .NET Native pass, rd.xml audit|V13
T40|.|ship: appx signed, GitHub repo + AI.md disclosure, README recipe|-

## §B BUGS

id|date|cause|fix
B1|2026-07-20|**Root cause: `JToken.Value<T>()` generic reflection fast-fails under .NET Native ARM** (uncatchable, no managed exc, no WER dump). Manifested 2 ways, both mistaken for socket bugs: (a) sending a UDP broadcast → OWN packet echoes to the 1716 listener → OnUdpMessageReceived parses it → `DeviceInfo.FromIdentityPacket` calls `GetString/GetInt/GetArray` (Value<T>) → death; looked like "send fatal" b/c crash always trailed a send. (b) real desktop identity received → `NetworkPacket.Deserialize`'s `Value<long>/<string>` → death between udp-line & udp-parsed. FIX: replace ALL `Value<T>()`/`Values<T>()` with explicit JToken casts + JTokenType checks (NetworkPacket accessors, Deserialize, DeviceInfo.ToSet). Verified on-device: full pipeline runs (udp-rx→parse→TCP dial→identity send→TLS-server handshake→v8 encrypted-identity parse) + GSConnect discovers phone. §B1 earlier "UDP-send fatal / §V21" was WRONG — retracted.|V22
B2|2026-07-20|Always-on SocketActivityTrigger (T30) rebroadcasts on every wake -> desktop reconnects -> socket closes -> SocketClosed wake -> rebroadcast: RECONNECT STORM (1776 handshakes/2049 wakes per run). Hammered desktop mousepad plugin (keyboardstate spam) -> GSConnect GC crash-block flood, 'enabling Mousepad breaks Zorin Connect'. FIX interim: `KdeConnectCore.EnableSocketActivity=false` (keep ExtendedExecution only). T30 needs loop-free redo: wake must process inbound WITHOUT rebroadcast; rate-limit; only transfer on real suspend.|V23

