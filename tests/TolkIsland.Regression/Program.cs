using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TolkIsland.Services;

namespace TolkIsland.Regression;

internal static class Program
{
    private static readonly List<(string Name, Func<Task> Run)> Cases = new();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--seed-failure")
        {
            Register("seeded failure", () => throw new InvalidOperationException("seeded"));
            return RunAll();
        }

        if (args.Length > 0 && args[0] == "--render-island")
        {
            return IslandRender.Run(
                args.Length > 1 ? args[1] : "demo/captures",
                args.Length > 2 ? args[2] : null,
                args.Length > 3 ? args[3] : null);
        }

        if (args.Length > 0 && args[0] == "--render-promo")
        {
            return IslandRender.RunPromo(
                args.Length > 1 ? args[1] : "promo/stills",
                args.Length > 2 ? args[2] : null);
        }

        if (args.Length > 0 && args[0] == "--render-screens")
        {
            return IslandRender.RunScreens(
                args.Length > 1 ? args[1] : "demo/screens",
                args.Length > 2 ? args[2] : null,
                args.Length > 3 ? args[3] : null);
        }

        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "tolkisland-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        SettingsService.OverrideDataDirectory(dataDirectory);
        DiagnosticsService.RequestLoggingEnabled = false;

        RegisterContracts();
        RegisterSettings();
        RegisterNotes();
        RegisterMedia();
        RegisterUpdates();
        RegisterLocalApi();
        RegisterDiagnostics();

        Console.WriteLine($"Tolk Island regression · {AppInfo.Version} · {dataDirectory}");
        var failures = RunAll();

        try
        {
            SettingsService.OverrideDataDirectory(null);
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch
        {
        }

        return failures;
    }

    private static int RunAll()
    {
        var failures = 0;
        var worker = new Thread(() =>
        {
            foreach (var (name, run) in Cases)
            {
                try
                {
                    run().GetAwaiter().GetResult();
                    Console.WriteLine($"PASS  {name}");
                }
                catch (Exception exception)
                {
                    failures++;
                    Console.WriteLine($"FAIL  {name}");
                    Console.WriteLine($"      {exception.GetType().Name}: {exception.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "Tolk Island regression"
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!worker.Join(TimeSpan.FromMinutes(4)))
        {
            Console.WriteLine("FAIL  harness timed out");
            return 1;
        }

        Console.WriteLine(failures == 0
            ? $"All {Cases.Count} cases passed."
            : $"{failures} of {Cases.Count} cases failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void RegisterContracts()
    {
        Register("save result maps known failures", () =>
        {
            Equal(SaveFailure.AccessDenied, SaveResult.From(new UnauthorizedAccessException()).Failure, "access denied");
            Equal(SaveFailure.DiskFull, SaveResult.From(new IOException("full", unchecked((int)0x80070070))).Failure, "disk full");
            Equal(SaveFailure.Serialization, SaveResult.From(new JsonException()).Failure, "serialization");
            Equal(SaveFailure.Unknown, SaveResult.From(new InvalidOperationException()).Failure, "unknown");
            AssertTrue(SaveResult.Success.Ok && SaveResult.Success.Message.Length == 0, "success is empty");
            return Task.CompletedTask;
        });

        Register("trusted meeting hosts are exact", () =>
        {
            AssertTrue(SafeUrl.IsTrustedMeeting("https://teams.microsoft.com/l/meetup-join/x", out _), "teams");
            AssertTrue(SafeUrl.IsTrustedMeeting("https://emea.zoom.us/j/123", out _), "zoom subdomain");
            AssertTrue(SafeUrl.IsTrustedMeeting("https://meet.google.com/abc-defg-hij", out _), "meet");
            AssertTrue(!SafeUrl.IsTrustedMeeting("https://attacker.example/?teams.microsoft.com", out _), "query trick");
            AssertTrue(!SafeUrl.IsTrustedMeeting("https://teams.microsoft.com@evil.example/", out _), "userinfo trick");
            AssertTrue(!SafeUrl.IsTrustedMeeting("https://teams.microsoft.com.evil.example/", out _), "suffix trick");
            AssertTrue(!SafeUrl.IsTrustedMeeting("file:///c:/teams.microsoft.com", out _), "file scheme");
            AssertTrue(!SafeUrl.IsTrustedMeeting("not a url", out _), "garbage");
            return Task.CompletedTask;
        });

        Register("private network endpoints stay local or private", () =>
        {
            AssertTrue(SafeUrl.IsLoopbackOrPrivate("http://127.0.0.1:11434/api/generate", out _), "loopback");
            AssertTrue(SafeUrl.IsLoopbackOrPrivate("http://localhost:8080/", out _), "localhost");
            AssertTrue(SafeUrl.IsLoopbackOrPrivate("http://192.168.1.20:8080/", out _), "192.168");
            AssertTrue(SafeUrl.IsLoopbackOrPrivate("http://10.0.0.45/", out _), "10.x");
            AssertTrue(SafeUrl.IsLoopbackOrPrivate("http://172.16.4.3/", out _), "172.16");
            AssertTrue(SafeUrl.IsLoopbackOrPrivate("http://desk.local/", out _), "mdns");
            AssertTrue(!SafeUrl.IsLoopbackOrPrivate("https://api.example.com/", out _), "public dns");
            AssertTrue(!SafeUrl.IsLoopbackOrPrivate("http://172.32.0.1/", out _), "172.32 public");
            AssertTrue(!SafeUrl.IsLoopbackOrPrivate("http://11.0.0.1/", out _), "11.x public");
            AssertTrue(!SafeUrl.IsLoopbackOrPrivate("http://user@127.0.0.1/", out _), "userinfo");
            return Task.CompletedTask;
        });

        Register("notification queue drops the oldest", () =>
        {
            var queue = new NotificationQueue<int>(3);
            Equal(3, queue.Capacity, "capacity floor");

            AssertTrue(queue.TryEnqueue(1), "first");
            AssertTrue(queue.TryEnqueue(2), "second");
            AssertTrue(queue.TryEnqueue(3), "third");
            AssertTrue(!queue.TryEnqueue(4), "overflow reports a drop");
            Equal(3, queue.Count, "count stays at capacity");

            AssertTrue(queue.TryDequeue(out var a) && a == 2, "oldest dropped");
            AssertTrue(queue.TryDequeue(out var b) && b == 3, "second");
            AssertTrue(queue.TryDequeue(out var c) && c == 4, "third");
            AssertTrue(!queue.TryDequeue(out _), "empty");

            queue.TryEnqueue(9);
            queue.Clear();
            Equal(0, queue.Count, "clear");

            var floor = new NotificationQueue<int>(0);
            Equal(1, floor.Capacity, "capacity never zero");
            return Task.CompletedTask;
        });

        Register("capture lease refcounts and generations", () =>
        {
            var lease = new CaptureLease();
            Equal(0, lease.Count, "starts empty");

            AssertTrue(lease.Acquire(), "first acquire owns capture");
            AssertTrue(!lease.Acquire(), "second acquire reuses capture");
            Equal(2, lease.Count, "two owners");
            AssertTrue(!lease.Release(), "first release keeps capture");
            AssertTrue(lease.Release(), "last release stops capture");
            Equal(0, lease.Count, "released");

            var generation = lease.StartSession();
            AssertTrue(lease.IsCurrent(generation), "current generation");
            lease.Release();
            var stopped = lease.Acquire();
            AssertTrue(stopped, "reacquire after stop");
            lease.Release();
            AssertTrue(!lease.IsCurrent(generation), "stale generation after stop");
            AssertTrue(!lease.Release(), "release below zero is ignored");
            return Task.CompletedTask;
        });

        Register("app info is populated", () =>
        {
            AssertTrue(AppInfo.Version.Length > 0, "version");
            AssertTrue(!AppInfo.Version.Contains('+'), "version has no revision metadata");
            Equal(2, AppInfo.ApiVersion, "api version");
            return Task.CompletedTask;
        });
    }

    private static void RegisterSettings()
    {
        Register("settings storage round-trips", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "roundtrip", "settings.json");
            var settings = new AppSettings
            {
                MonitorMode = MonitorMode.All,
                ExpandMode = ExpandMode.Click,
                QuickLaunch = new List<string> { "C:/Tools/Notes.exe" },
                NotificationSounds = true,
                HapticsEnabled = true,
                Scale = 1.1,
                Skin = SkinMode.Spider
            };

            var saved = SettingsStorage.Save(path, settings);
            AssertTrue(saved.Ok, "save: " + saved.Message);

            var loaded = SettingsStorage.Load(path);
            Equal(MonitorMode.All, loaded.MonitorMode, "monitor mode");
            Equal(ExpandMode.Click, loaded.ExpandMode, "expand mode");
            Equal("C:/Tools/Notes.exe", loaded.QuickLaunch[0], "quick launch");
            Equal(SkinMode.Spider, loaded.Skin, "skin");
            AssertTrue(loaded.NotificationSounds, "sounds");
            AssertTrue(loaded.HapticsEnabled, "haptics");
            AssertTrue(Math.Abs(loaded.Scale - 1.1) < 0.001, "scale");
            return Task.CompletedTask;
        });

        Register("corrupt settings fall back to defaults", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "corrupt", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");
            var loaded = SettingsStorage.Load(path);
            Equal(MonitorMode.Primary, loaded.MonitorMode, "monitor mode");
            Equal(ThemeMode.Dark, loaded.Theme, "theme");
            AssertTrue(Math.Abs(loaded.Scale - 1.0) < 0.001, "scale");
            AssertTrue(loaded.MediaEnabled, "media default");
            return Task.CompletedTask;
        });

        Register("corrupt settings recover from the last good backup", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "recover", "settings.json");
            var first = SettingsStorage.Save(path, new AppSettings { LicenseKey = "first", Scale = 1.1 });
            AssertTrue(first.Ok, "first save: " + first.Message);
            var second = SettingsStorage.Save(path, new AppSettings { LicenseKey = "second", Scale = 1.2 });
            AssertTrue(second.Ok, "second save: " + second.Message);
            AssertTrue(File.Exists(path + ".bak"), "backup written before overwrite");

            File.WriteAllText(path, "{ broken");
            var loaded = SettingsStorage.Load(path);
            Equal("first", loaded.LicenseKey, "recovered licence");
            AssertTrue(Math.Abs(loaded.Scale - 1.1) < 0.001, "recovered scale");
            AssertTrue(File.Exists(path + ".bad"), "corrupt file quarantined");
            Equal("{ broken", File.ReadAllText(path + ".bad"), "quarantine keeps the original text");
            return Task.CompletedTask;
        });

        Register("shelf accepts local files only", () =>
        {
            var localPath = Path.Combine(SettingsService.DataDirectory, "shelf-local.txt");
            File.WriteAllText(localPath, "hi");

            AssertTrue(ShelfItem.FromPath(localPath).Exists, "local file accepted");
            AssertTrue(!ShelfItem.FromPath(@"\\server\share\notes.txt").Exists, "UNC path rejected");
            AssertTrue(!ShelfItem.FromPath(@"\\?\C:\Windows\notepad.exe").Exists, "device path rejected");
            AssertTrue(!ShelfItem.FromPath("notes.txt").Exists, "relative path rejected");
            return Task.CompletedTask;
        });

        Register("spider skin palette and web geometry", () =>
        {
            Equal(SpiderSkin.RedDark, SpiderSkin.AccentFor(SkinMode.Spider, true, "#FF0A84FF"), "dark suit accent");
            Equal(SpiderSkin.RedLight, SpiderSkin.AccentFor(SkinMode.Spider, false, "#FF0A84FF"), "light suit accent");
            Equal("#FF0A84FF", SpiderSkin.AccentFor(SkinMode.Classic, true, "#FF0A84FF"), "classic keeps user accent");

            var web = SpiderSkin.BuildWeb(460, 172);
            AssertTrue(web.IsFrozen, "web geometry frozen");
            AssertTrue(!web.IsEmpty(), "web has figures");

            var buddy = SpiderSkin.BuildBuddy(dark: false);
            AssertTrue(buddy.IsFrozen, "buddy image frozen");
            AssertTrue(buddy.Width > 0 && buddy.Height > 0, "buddy image has a design box");
            return Task.CompletedTask;
        });

        Register("settings storage reports a denied write", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "denied", "settings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                var result = SettingsStorage.Save(path, new AppSettings());
                AssertTrue(!result.Ok, "read-only target must fail");
                AssertTrue(result.Failure != SaveFailure.None, "failure is classified");
                AssertTrue(result.Message.Length > 0, "failure has a message");
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            return Task.CompletedTask;
        });

        Register("settings update applies, persists and notifies", () =>
        {
            SettingsService.Reset();
            var notifications = 0;
            void OnChanged() => notifications++;
            SettingsService.Changed += OnChanged;

            try
            {
                var result = SettingsService.Update(settings =>
                {
                    settings.MonitorMode = MonitorMode.All;
                    settings.ExpandMode = ExpandMode.Click;
                    settings.NotificationSounds = true;
                    settings.HapticsEnabled = true;
                    settings.QuickLaunch = new List<string> { "C:/Tools/App.exe" };
                });

                AssertTrue(result.Ok, "update saved: " + result.Message);
                Equal(1, notifications, "one change notification");
                AssertTrue(File.Exists(SettingsService.SettingsPath), "file exists");

                var loaded = SettingsStorage.Load(SettingsService.SettingsPath);
                Equal(MonitorMode.All, loaded.MonitorMode, "persisted monitor mode");
                Equal(ExpandMode.Click, loaded.ExpandMode, "persisted expand mode");
                Equal("C:/Tools/App.exe", loaded.QuickLaunch[0], "persisted quick launch");
            }
            finally
            {
                SettingsService.Changed -= OnChanged;
                SettingsService.Reset();
            }

            return Task.CompletedTask;
        });

        Register("settings normalization clamps out-of-range values", () =>
        {
            SettingsService.Reset();
            try
            {
                var result = SettingsService.Update(settings =>
                {
                    settings.Scale = 9;
                    settings.CornerRadius = -5;
                    settings.NotificationVisibleSeconds = 900;
                    settings.IdleHideSeconds = 0;
                    settings.Skin = (SkinMode)9;
                });

                AssertTrue(result.Ok, "saved");
                Equal(1.25, SettingsService.Current.Scale, "scale clamped");
                Equal(0.0, SettingsService.Current.CornerRadius, "radius clamped");
                Equal(30, SettingsService.Current.NotificationVisibleSeconds, "notification seconds");
                Equal(3, SettingsService.Current.IdleHideSeconds, "idle seconds");
                Equal(SkinMode.Classic, SettingsService.Current.Skin, "unknown skin falls back");
            }
            finally
            {
                SettingsService.Reset();
            }

            return Task.CompletedTask;
        });

        Register("failed settings update leaves memory and disk unchanged", () =>
        {
            SettingsService.Reset();
            var original = SettingsService.Current.Theme;
            var notifications = 0;
            void OnChanged() => notifications++;
            SettingsService.Changed += OnChanged;

            SaveResult result;
            try
            {
                using (new FileStream(
                    SettingsService.SettingsPath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None))
                {
                    result = SettingsService.Update(settings =>
                        settings.Theme = original == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);
                }
            }
            finally
            {
                SettingsService.Changed -= OnChanged;
            }

            AssertTrue(!result.Ok, "update reports failure");
            Equal(original, SettingsService.Current.Theme, "live settings unchanged");
            Equal(0, notifications, "no change notification for a failed save");
            Equal(
                original,
                SettingsStorage.Load(SettingsService.SettingsPath).Theme,
                "disk settings unchanged");
            SettingsService.Reset();
            return Task.CompletedTask;
        });

        Register("settings normalization tolerates null list entries", () =>
        {
            SettingsService.Reset();
            try
            {
                var result = SettingsService.Update(settings =>
                {
                    settings.BlockedApps = new List<string> { null!, "  discord.exe  " };
                    settings.PriorityApps = new List<string> { null! };
                });

                AssertTrue(result.Ok, "saved: " + result.Message);
                Equal(1, SettingsService.Current.BlockedApps.Count, "null blocked entry dropped");
                Equal("discord.exe", SettingsService.Current.BlockedApps[0], "trimmed entry");
                Equal(0, SettingsService.Current.PriorityApps.Count, "null priority entry dropped");
            }
            finally
            {
                SettingsService.Reset();
            }

            return Task.CompletedTask;
        });

        Register("failed reset keeps the current settings", () =>
        {
            SettingsService.Reset();
            var saved = SettingsService.Update(settings => settings.Theme = ThemeMode.Light);
            AssertTrue(saved.Ok, "seed: " + saved.Message);

            SaveResult result;
            using (new FileStream(
                SettingsService.SettingsPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                result = SettingsService.Reset();
            }

            AssertTrue(!result.Ok, "reset reports failure");
            Equal(ThemeMode.Light, SettingsService.Current.Theme, "live settings unchanged");
            Equal(
                ThemeMode.Light,
                SettingsStorage.Load(SettingsService.SettingsPath).Theme,
                "disk settings unchanged");
            SettingsService.Reset();
            return Task.CompletedTask;
        });

    }

    private static void RegisterNotes()
    {
        Register("notes persist and survive reload", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "notes.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var notes = new NotesService();
            var added = notes.Add("  buy milk  ");
            AssertTrue(added.Ok, "add: " + added.Message);
            Equal(1, notes.Items.Count, "one note");
            Equal("buy milk", notes.Items[0].Text, "trimmed");

            var reloaded = new NotesService();
            Equal(1, reloaded.Items.Count, "reload");
            Equal("buy milk", reloaded.Items[0].Text, "reloaded text");
            return Task.CompletedTask;
        });

        Register("notes roll back memory when the write fails", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "notes.json");
            var notes = new NotesService();
            var before = notes.Items.Count;
            var removedId = notes.Items[0].Id;

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var added = notes.Add("should not stick");
                AssertTrue(!added.Ok, "add reports failure");
                Equal(before, notes.Items.Count, "add rolled back");
                AssertTrue(notes.LastError.Length > 0, "add error surfaced");

                var removed = notes.Remove(removedId);
                AssertTrue(!removed.Ok, "remove reports failure");
                Equal(before, notes.Items.Count, "remove rolled back");
            }

            var afterUnlock = new NotesService();
            AssertTrue(afterUnlock.Items.Count == before, "disk still has the notes");
            return Task.CompletedTask;
        });

        Register("failed add at capacity keeps the evicted note in memory", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "notes.json");
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            var notes = new NotesService();
            for (var index = 0; index < 40; index++)
            {
                var added = notes.Add("note " + index);
                AssertTrue(added.Ok, "seed note " + index + ": " + added.Message);
            }

            Equal(40, notes.Items.Count, "at capacity");
            var oldest = notes.Items[^1].Text;
            Equal("note 0", oldest, "oldest note seeded");

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var overflow = notes.Add("overflow");
                AssertTrue(!overflow.Ok, "overflow add fails while locked");
            }

            Equal(40, notes.Items.Count, "memory still at capacity");
            AssertTrue(
                notes.Items.Any(note => note.Text == oldest),
                "capacity-evicted note is still in memory");

            var reloaded = new NotesService();
            Equal(40, reloaded.Items.Count, "disk still has 40 notes");
            return Task.CompletedTask;
        });

        Register("corrupt notes file is quarantined, not overwritten", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "notes.json");
            foreach (var stale in Directory.GetFiles(
                         SettingsService.DataDirectory,
                         "notes.corrupt-*.json"))
            {
                File.Delete(stale);
            }

            File.WriteAllText(path, "{ not json");
            var notes = new NotesService();

            AssertTrue(!File.Exists(path), "corrupt file moved aside");
            var quarantined = Directory.GetFiles(
                SettingsService.DataDirectory,
                "notes.corrupt-*.json");
            Equal(1, quarantined.Length, "one quarantine copy");
            Equal("{ not json", File.ReadAllText(quarantined[0]), "original bytes preserved");
            AssertTrue(notes.LastError.Length > 0, "error surfaced");
            Equal(0, notes.Items.Count, "starts empty");

            var added = notes.Add("fresh start");
            AssertTrue(added.Ok, "writes work after quarantine: " + added.Message);
            AssertTrue(File.Exists(path), "new notes file created");
            return Task.CompletedTask;
        });

        Register("unreadable notes file blocks writes instead of losing data", () =>
        {
            var path = Path.Combine(SettingsService.DataDirectory, "notes.json");
            const string original = "[{\"Text\":\"keep me\"}]";
            File.WriteAllText(path, original);

            NotesService notes;
            SaveResult added;
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                notes = new NotesService();
                added = notes.Add("must not overwrite");
            }

            AssertTrue(!added.Ok, "write blocked while the original cannot be read");
            Equal(original, File.ReadAllText(path), "original bytes untouched");

            var reloaded = new NotesService();
            Equal(1, reloaded.Items.Count, "reloads once the lock is gone");
            Equal("keep me", reloaded.Items[0].Text, "original note intact");
            return Task.CompletedTask;
        });
    }

    private static void RegisterMedia()
    {
        Register("media clock advances past ten minutes and follows playback rate", () =>
        {
            var start = DateTimeOffset.UtcNow;

            var accelerated = MediaSessionService.AdvanceTimeline(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromHours(2),
                isPlaying: true,
                playbackRate: 1.5,
                lastUpdated: start,
                now: start.AddMinutes(30));
            Equal(TimeSpan.FromMinutes(46), accelerated.Position, "30 minutes at 1.5x");
            Equal(TimeSpan.FromHours(2), accelerated.Duration, "duration unchanged");

            var clamped = MediaSessionService.AdvanceTimeline(
                TimeSpan.FromMinutes(119),
                TimeSpan.FromHours(2),
                isPlaying: true,
                playbackRate: 1.0,
                lastUpdated: start,
                now: start.AddMinutes(10));
            Equal(TimeSpan.FromHours(2), clamped.Position, "position clamped to duration");

            var sleepGap = MediaSessionService.AdvanceTimeline(
                TimeSpan.FromMinutes(5),
                TimeSpan.FromHours(2),
                isPlaying: true,
                playbackRate: 1.0,
                lastUpdated: start,
                now: start.AddHours(9));
            Equal(TimeSpan.FromMinutes(5), sleepGap.Position, "sleep gap is not playback");
            return Task.CompletedTask;
        });

        Register("media change detection ignores repeated snapshots", () =>
        {
            var artA = new byte[] { 1, 2, 3 };
            var artB = new byte[] { 4, 5, 6 };
            var first = new MediaInfo("Track A", "Artist", "Spotify", true, true, artA);
            var repeated = new MediaInfo("Track A", "Artist", "Spotify", true, true, artA);
            var next = new MediaInfo("Track B", "Artist", "Spotify", true, true, artB);

            AssertTrue(MediaSessionService.SameMedia(first, repeated), "identical snapshot is not a change");
            AssertTrue(!MediaSessionService.SameMedia(first, next), "new title is a change");
            AssertTrue(!MediaSessionService.SameMedia(first, first with { IsPlaying = false }), "pause is a change");
            AssertTrue(!MediaSessionService.SameMedia(first, first with { Thumbnail = artB }), "new art is a change");
            AssertTrue(!MediaSessionService.SameMedia(first, first with { SourceApp = "Chrome" }), "new app is a change");
            AssertTrue(MediaSessionService.SameMedia(null, null), "no media equals no media");
            AssertTrue(!MediaSessionService.SameMedia(first, null), "media to none is a change");
            return Task.CompletedTask;
        });
    }

    private static void RegisterUpdates()
    {
        Register("update check rejects missing or invalid configuration", async () =>
        {
            SettingsService.Reset();
            var disabled = await UpdateService.CheckAsync();
            Equal(UpdateCheckStatus.NotConfigured, disabled.Status, "disabled");
            AssertTrue(disabled.Message.Length > 0, "message");

            SettingsService.Update(settings =>
            {
                settings.UpdateCheckEnabled = true;
                settings.UpdateManifestUrl = "not a web address";
            });

            var invalid = await UpdateService.CheckAsync();
            Equal(UpdateCheckStatus.Failed, invalid.Status, "invalid url");
            AssertTrue(invalid.Message.Contains("valid web address", StringComparison.OrdinalIgnoreCase), "invalid url copy");
        });

        Register("update check handles every HTTP outcome", async () =>
        {
            using var server = new TestServer();
            server.Handler = context =>
            {
                var path = context.Request.Url?.AbsolutePath ?? "/";
                return path switch
                {
                    "/404" => WriteText(context, 404, "{}"),
                    "/bad" => WriteText(context, 200, "{ nope"),
                    "/empty" => WriteText(context, 200, string.Empty),
                    "/old" => WriteText(context, 200, """{"version":"0.9.0","url":"https://example.com/app.exe"}"""),
                    "/new" => WriteText(context, 200, """{"version":"9.9.9","url":"https://example.com/app.exe","notes":"big"}"""),
                    "/filedl" => WriteText(context, 200, """{"version":"9.9.9","url":"file:///c:/app.exe"}"""),
                    "/big" => WriteText(context, 200, new string('x', 9000)),
                    _ => WriteText(context, 404, "{}")
                };
            };

            SettingsService.Update(settings => settings.UpdateCheckEnabled = true);

            async Task<UpdateCheckResult> Check(string route)
            {
                SettingsService.Update(settings => settings.UpdateManifestUrl = server.BaseUrl + route);
                return await UpdateService.CheckAsync();
            }

            var notFound = await Check("404");
            Equal(UpdateCheckStatus.Failed, notFound.Status, "404 status");
            AssertTrue(notFound.Message.Contains("404", StringComparison.Ordinal), "404 copy");

            var bad = await Check("bad");
            AssertTrue(bad.Message.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase), "json copy");

            var empty = await Check("empty");
            AssertTrue(empty.Message.Contains("empty", StringComparison.OrdinalIgnoreCase), "empty copy");

            var older = await Check("old");
            Equal(UpdateCheckStatus.UpToDate, older.Status, "older version");

            var newer = await Check("new");
            Equal(UpdateCheckStatus.UpdateAvailable, newer.Status, "newer version");
            Equal("9.9.9", newer.Update?.Version ?? string.Empty, "available version");
            Equal("big", newer.Update?.Notes ?? string.Empty, "notes");

            var fileDownload = await Check("filedl");
            AssertTrue(fileDownload.Message.Contains("download link", StringComparison.OrdinalIgnoreCase), "scheme copy");
            AssertTrue(UpdateService.Available is null, "no available update after failure");

            var big = await Check("big");
            AssertTrue(big.Message.Contains("8 KB", StringComparison.OrdinalIgnoreCase), "size copy");
        });
    }

    private const string ApiToken = "test-token-0123456789abcdef0123456789abcdef";

    private static void RegisterLocalApi()
    {
        Register("local API serves health and status", async () =>
        {
            var port = TestServer.FreePort();
            using var api = new LocalApiService(port, ApiToken);
            var paused = false;
            api.PausedProvider = () => paused;
            api.Start();
            AssertTrue(api.IsRunning, "listener started");

            using var http = CreateClient();
            using var health = await http.GetAsync(api.BaseUrl + "health");
            Equal(HttpStatusCode.OK, health.StatusCode, "health status");
            using var healthJson = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
            AssertTrue(healthJson.RootElement.GetProperty("ok").GetBoolean(), "health ok");
            Equal(AppInfo.ApiVersion, healthJson.RootElement.GetProperty("apiVersion").GetInt32(), "api version");
            Equal(AppInfo.Version, healthJson.RootElement.GetProperty("version").GetString() ?? string.Empty, "version");

            using var anonymous = CreateClient(token: null);
            using var unauthorized = await anonymous.GetAsync(api.BaseUrl + "status");
            Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode, "token required");
            using var unauthorizedJson = JsonDocument.Parse(await unauthorized.Content.ReadAsStringAsync());
            Equal("unauthorized", unauthorizedJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "token code");

            using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, api.BaseUrl + "status");
            crossOrigin.Headers.Add("Origin", "https://example.com");
            using var originResponse = await http.SendAsync(crossOrigin);
            Equal(HttpStatusCode.Unauthorized, originResponse.StatusCode, "cross-origin refused");

            paused = true;
            using var status = await http.GetAsync(api.BaseUrl + "status");
            using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            AssertTrue(statusJson.RootElement.GetProperty("paused").GetBoolean(), "paused status");
            AssertTrue(statusJson.RootElement.GetProperty("uptimeSeconds").GetInt64() >= 0, "uptime");
        });

        Register("local API bounds request bodies", async () =>
        {
            var port = TestServer.FreePort();
            using var api = new LocalApiService(port, ApiToken);
            api.Start();
            AssertTrue(api.IsRunning, "listener started");

            using var http = CreateClient();

            using var declared = await http.PostAsync(
                api.BaseUrl + "activity",
                new StringContent(new string('x', 9000), Encoding.UTF8, "application/json"));
            Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode, "declared 9 KB");
            using var declaredJson = JsonDocument.Parse(await declared.Content.ReadAsStringAsync());
            Equal("body_too_large", declaredJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "declared code");

            var chunked = SendRaw(port,
                "POST /activity HTTP/1.1\r\n" +
                $"Host: 127.0.0.1:{port}\r\n" +
                $"X-Tolk-Token: {ApiToken}\r\n" +
                "Content-Type: application/json\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n" +
                "2328\r\n" + new string('x', 9000) + "\r\n0\r\n\r\n");
            AssertTrue(chunked.Contains(" 413 ", StringComparison.Ordinal), "chunked 9 KB: " + FirstLine(chunked));
        });

        Register("local API reports malformed and unknown requests", async () =>
        {
            var port = TestServer.FreePort();
            using var api = new LocalApiService(port, ApiToken);
            api.Start();
            using var http = CreateClient();

            using var malformed = await http.PostAsync(
                api.BaseUrl + "activity",
                new StringContent("{ nope", Encoding.UTF8, "application/json"));
            Equal(HttpStatusCode.BadRequest, malformed.StatusCode, "malformed status");
            using var malformedJson = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync());
            Equal("bad_json", malformedJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "malformed code");

            using var unknownPanel = await http.PostAsync(
                api.BaseUrl + "show",
                new StringContent("""{"panel":"nope"}""", Encoding.UTF8, "application/json"));
            Equal(HttpStatusCode.BadRequest, unknownPanel.StatusCode, "unknown panel status");
            using var panelJson = JsonDocument.Parse(await unknownPanel.Content.ReadAsStringAsync());
            Equal("unknown_panel", panelJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "unknown panel code");

            using var missing = await http.GetAsync(api.BaseUrl + "nope");
            Equal(HttpStatusCode.NotFound, missing.StatusCode, "unknown route status");
            using var missingJson = JsonDocument.Parse(await missing.Content.ReadAsStringAsync());
            Equal("not_found", missingJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "unknown route code");
        });

        Register("local API enforces pause and feature policy", async () =>
        {
            var port = TestServer.FreePort();
            using var api = new LocalApiService(port, ApiToken);
            var paused = false;
            var notificationsEnabled = true;
            api.PausedProvider = () => paused;
            api.FeatureProvider = feature => feature switch
            {
                "notifications" => notificationsEnabled,
                _ => true
            };
            api.Start();
            using var http = CreateClient();

            paused = true;
            using var pausedResponse = await http.PostAsync(
                api.BaseUrl + "activity",
                new StringContent("""{"title":"hi"}""", Encoding.UTF8, "application/json"));
            Equal(HttpStatusCode.Conflict, pausedResponse.StatusCode, "paused status");
            using var pausedJson = JsonDocument.Parse(await pausedResponse.Content.ReadAsStringAsync());
            Equal("paused", pausedJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "paused code");

            paused = false;
            notificationsEnabled = false;
            using var disabledResponse = await http.PostAsync(
                api.BaseUrl + "activity",
                new StringContent("""{"title":"hi"}""", Encoding.UTF8, "application/json"));
            Equal(HttpStatusCode.Forbidden, disabledResponse.StatusCode, "disabled status");
            using var disabledJson = JsonDocument.Parse(await disabledResponse.Content.ReadAsStringAsync());
            Equal("disabled", disabledJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "disabled code");

            var delivered = 0;
            api.ActivityReceived += _ => Interlocked.Increment(ref delivered);
            notificationsEnabled = true;
            using var accepted = await http.PostAsync(
                api.BaseUrl + "activity",
                new StringContent("""{"title":"hi"}""", Encoding.UTF8, "application/json"));
            Equal(HttpStatusCode.Accepted, accepted.StatusCode, "accepted status");
            AssertTrue(Volatile.Read(ref delivered) == 1, "activity delivered once");
        });

        Register("local API caps concurrency at eight", async () =>
        {
            var port = TestServer.FreePort();
            using var api = new LocalApiService(port, ApiToken);
            api.Start();
            AssertTrue(api.IsRunning, "listener started");

            var sockets = new List<TcpClient>();
            try
            {
                for (var index = 0; index < 8; index++)
                {
                    var socket = new TcpClient();
                    socket.Connect(IPAddress.Loopback, port);
                    var headers = Encoding.ASCII.GetBytes(
                        "POST /activity HTTP/1.1\r\n" +
                        $"Host: 127.0.0.1:{port}\r\n" +
                        $"X-Tolk-Token: {ApiToken}\r\n" +
                        "Content-Type: application/json\r\n" +
                        "Content-Length: 100\r\n\r\n");
                    socket.GetStream().Write(headers, 0, headers.Length);
                    socket.GetStream().Flush();
                    sockets.Add(socket);
                }

                Thread.Sleep(400);

                using var http = CreateClient();
                using var busy = await http.GetAsync(api.BaseUrl + "health");
                Equal((HttpStatusCode)429, busy.StatusCode, "ninth request is busy");
                using var busyJson = JsonDocument.Parse(await busy.Content.ReadAsStringAsync());
                Equal("busy", busyJson.RootElement.GetProperty("code").GetString() ?? string.Empty, "busy code");
            }
            finally
            {
                foreach (var socket in sockets)
                {
                    socket.Dispose();
                }

                Thread.Sleep(200);
            }
        });
    }

    private static void RegisterDiagnostics()
    {
        Register("diagnostics rotates the log and records the build", () =>
        {
            foreach (var file in Directory.EnumerateFiles(SettingsService.DataDirectory, "crash.log*"))
            {
                File.Delete(file);
            }

            DiagnosticsService.RequestLoggingEnabled = true;
            try
            {
                var line = new string('x', 4096);
                for (var index = 0; index < 300; index++)
                {
                    DiagnosticsService.WriteLine("test", line);
                }

                DiagnosticsService.Write("test", new Exception("boom"));

                AssertTrue(File.Exists(DiagnosticsService.LogPath), "current log exists");
                AssertTrue(File.Exists(DiagnosticsService.LogPath + ".1"), "rotated log exists");

                var current = new FileInfo(DiagnosticsService.LogPath);
                AssertTrue(current.Length < 1024 * 1024, "current log below the cap");

                var text = File.ReadAllText(DiagnosticsService.LogPath);
                AssertTrue(text.Contains("System.Exception: boom", StringComparison.Ordinal), "exception recorded");
                AssertTrue(text.Contains(" v" + AppInfo.Version, StringComparison.Ordinal), "version recorded");
                AssertTrue(text.Contains(Environment.OSVersion.VersionString, StringComparison.Ordinal), "os recorded");
                AssertTrue(!text.Contains("https://", StringComparison.Ordinal), "no urls in the block");
            }
            finally
            {
                DiagnosticsService.RequestLoggingEnabled = false;
            }

            return Task.CompletedTask;
        });
    }

    private static void Register(string name, Func<Task> run) => Cases.Add((name, run));

    private static void AssertTrue(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void Equal<T>(T expected, T actual, string what)
        where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{what}: expected \"{expected}\", got \"{actual}\"");
        }
    }

    private static HttpClient CreateClient(string? token = ApiToken)
    {
        var client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        if (token is not null)
        {
            client.DefaultRequestHeaders.Add(LocalApiService.TokenHeader, token);
        }

        return client;
    }

    private static Task WriteText(HttpListenerContext context, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        return context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    }

    private static string SendRaw(int port, string request)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        client.ReceiveTimeout = 5000;
        using var stream = client.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        var buffer = new byte[1024];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static string FirstLine(string response)
    {
        var index = response.IndexOf('\r');
        return index >= 0 ? response[..index] : response;
    }

    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private int _hits;

        public TestServer()
        {
            var port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public string BaseUrl { get; }

        public int Hits => Volatile.Read(ref _hits);

        public Func<HttpListenerContext, Task>? Handler { get; set; }

        public static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested && _listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                Interlocked.Increment(ref _hits);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (Handler is { } handler)
                        {
                            await handler(context).ConfigureAwait(false);
                        }
                        else
                        {
                            context.Response.StatusCode = 404;
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        try
                        {
                            context.Response.Close();
                        }
                        catch
                        {
                        }
                    }
                });
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch
            {
            }

            try
            {
                _loop.Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }

            _stop.Dispose();
        }
    }
}
