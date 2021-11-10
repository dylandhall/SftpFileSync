using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FileWatcherLocal
{
    class Program
    {
        private static string _root = "";
        private static string _remoteRoot = "";
        
        static async Task Main(string[] args)
        {
            var setttingsFile = Directory.GetCurrentDirectory() + @"\connectionSettings.json";
            ConnectionSettings settings = null;
            if (File.Exists(setttingsFile) && !args.Contains("-clear"))
            {
                try
                {
                    settings = JsonSerializer.Deserialize<ConnectionSettings>(await File.ReadAllTextAsync(setttingsFile));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Can't read settings: {ex.Message}");
                    File.Delete(setttingsFile);
                }

                if (settings != null)
                {
                    Console.WriteLine($"Local root {settings.Root}, remote root {settings.RemoteRoot}");
                    if (settings.Folders.Any())
                        Console.WriteLine($"Watching subfolders {string.Join(", ", settings.Folders)}");
                }
            }

            if (settings == null)
            {
                settings = new ConnectionSettings();
                
                Console.WriteLine("Select root local folder");
                settings.Root = Console.ReadLine().Trim('\\').Replace('/', '\\');
                while (!Directory.Exists(settings.Root))
                {
                    Console.WriteLine("Please enter a valid folder");
                    settings.Root = Console.ReadLine();
                }
                Console.WriteLine("Select remote root folder - case matters for linux systems, this includes subfolders");
                settings.RemoteRoot = Console.ReadLine();
                Console.WriteLine("Select subfolders to watch (enter when done, or just enter to watch whole root folder)");
                var folders = new List<string>();
                var done = false;
                while (!done)
                {
                    var output = "\\" + (Console.ReadLine().Trim('\\').Replace('/', '\\'));
                    done = output == "\\";
                    if (done) continue;
                    
                    if (!Directory.Exists($"{settings.Root}{output}"))
                        Console.WriteLine($"Folder {settings.Root}{output} does not exist");
                    else
                        folders.Add(output);
                }
                settings.Folders = folders.ToArray();
                
                await File.WriteAllTextAsync(setttingsFile, JsonSerializer.Serialize(settings));
            }

            _remoteRoot = settings.RemoteRoot;
            _root = settings.Root;
            
            //async monitors for keypresses
            //ghetto
            Task.Run(() => MonitorKeypress());

            while (!hasQuit) //if there's an error, we just reconnect and keep going
            {
                Console.WriteLine();
                Console.WriteLine("******** STARTING **********");
                Console.WriteLine();

                var watchers = settings.Folders.Any()
                    ? settings.Folders.Select(folder => new FileSystemWatcher(settings.Root + folder)).ToList()
                    : new List<FileSystemWatcher> { new (settings.Root) };
            
                try
                {
                    watchers.ForEach(watcher =>
                    {
                        watcher.IncludeSubdirectories = true;
                        watcher.Changed += Watcher_Update;
                        watcher.Deleted += Watcher_Update;
                        watcher.Created += Watcher_Update;
                        watcher.Renamed += Watcher_Renamed;
                        watcher.EnableRaisingEvents = true;
                    });
                    while (!hasQuit)
                    {
                        await Task.Delay(100);
                        await HandleEvents().ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);

                    var time = DateTimeOffset.Now.ToLocalTime().ToString();
                    foreach (var c in Path.GetInvalidFileNameChars())
                    {
                        time = time.Replace(c, '-');
                    }
                    File.WriteAllLines($"C:\\temp\\filewatcher-error-{time}.txt", new[] { ex.Message, Environment.NewLine, ex.StackTrace });
                    if (onProbation)
                    {
                        UpdateEvents = new ConcurrentBag<FileEvent>();
                        AlternateUpdateEvents = new ConcurrentBag<FileEvent>();
                        onProbation = false;
                    } 
                    else
                        onProbation = true;
                }
                finally
                {
                    watchers.ForEach(watcher => watcher.Dispose());
                    watchers = null;
                }
            }
        }

        private static bool hasQuit = false;

        private static void MonitorKeypress()
        {
            ConsoleKeyInfo cki = new ConsoleKeyInfo();
            do
            {
                // true hides the pressed character from the console
                cki = Console.ReadKey(true);

                // Wait for an ESC or q
            } while (cki.Key != ConsoleKey.Escape && cki.Key != ConsoleKey.Q);
            hasQuit = true;
        }

        private static bool clearingCollections = false;
        private static async Task HandleEvents()
        {
            var now = DateTimeOffset.UtcNow;
            var timeout = TimeSpan.FromMilliseconds(100);

            var eToHandle = UpdateEvents.Where(a => !a.Handled && now - a.Time > timeout).ToList();

            //if events have occurred recently, they might be part of a series of updates that hasn't completed yet.
            //we'll wait until we have a brief period of inactivity before we process the events.

            bool IsEventTooRecent(FileEvent fileEvent) => now - fileEvent.Time < timeout;

            if (eToHandle.Any(IsEventTooRecent))
                return;

            eToHandle.ForEach(SetHandled);

            var dirToCreate = new List<string>();
            var toCreate = new List<string>();
            var toDelete = new List<string>();

            foreach (var e in eToHandle.OrderBy(a => a.Time))
            {
                if (e.IsRename)
                {
                    if (!toCreate.Contains(e.RnEvent.OldFullPath) && !dirToCreate.Contains(e.RnEvent.OldFullPath))
                    {
                        toDelete.Add(e.RnEvent.OldFullPath);
                    }
                    toCreate.RemoveAll(a => a == e.RnEvent.OldFullPath);
                    dirToCreate.RemoveAll(a => a == e.RnEvent.OldFullPath);
                    toDelete.RemoveAll(a => a == e.RnEvent.FullPath);
                    if (Directory.Exists(e.RnEvent.FullPath))
                        dirToCreate.Add(e.RnEvent.FullPath);
                    else
                        toCreate.Add(e.RnEvent.FullPath);
                }
                else
                {
                    switch (e.Event.ChangeType) {
                        case WatcherChangeTypes.Created:
                            toDelete.RemoveAll(a => a == e.Event.FullPath);
                            if (Directory.Exists(e.Event.FullPath))
                                dirToCreate.Add(e.Event.FullPath);
                            else
                                toCreate.Add(e.Event.FullPath);
                            break;
                        case WatcherChangeTypes.Deleted:
                            if (toCreate.Contains(e.Event.FullPath))
                                toCreate.RemoveAll(a => a == e.Event.FullPath);
                            else if (dirToCreate.Contains(e.Event.FullPath))
                                dirToCreate.RemoveAll(a => a == e.Event.FullPath);
                            else
                                toDelete.Add(e.Event.FullPath);
                            break;
                        case WatcherChangeTypes.Changed:
                            toCreate.Add(e.Event.FullPath);
                            break;
                        default:
                            break;
                    }
                }
            }

            if (UpdateEvents.Count > 200)
            {
                clearingCollections = true;
                var old = UpdateEvents;
                UpdateEvents = new ConcurrentBag<FileEvent>();
                clearingCollections = false;
                old.Where(a => !a.Handled).ToList().ForEach(a => UpdateEvents.Add(a));
            }

            string asString(List<string> s) => string.Join(", ", s.Distinct().ToList());
            bool hasChanges = toCreate.Any() || toDelete.Any() || dirToCreate.Any();
            if (!hasChanges) return;

            Console.WriteLine(Environment.NewLine);
            Console.WriteLine("** Changes detected, running update **");

            if (dirToCreate.Any()) Console.WriteLine($"Creating directory {asString(dirToCreate)}");
            if (toDelete.Any()) Console.WriteLine($"Deleting {asString(toDelete)}");
            if (toCreate.Any()) Console.WriteLine($"Creating {asString(toCreate)}");

            var watch = new Stopwatch();

            watch.Start();

            var createDirTasks = dirToCreate.Select(file =>
                Task.Run(() =>
                {
                    try
                    {
                        Directory.CreateDirectory(_remoteRoot + file[_root.Length..]);
                    }
                    catch(Exception ex)
                    {
                        Console.WriteLine($"Error creating folder {file}: {ex.Message}");
                    }
                })).ToList();

            await Task.WhenAll(createDirTasks);

            var createFileTasks =
                toCreate.Select(file =>
                Task.Run(() =>
                { 
                    File.Copy(file, _remoteRoot + file[_root.Length..], true);
                })).ToList();

            var deleteFileTasks = toDelete
                .OrderByDescending(file => file.Split('\\').Last().Contains('.')) //makes it more likely we'll attempt folder deletion after the files in it have been deleted
                .Select(file =>
                Task.Run(() =>
                {
                    try
                    {
                        File.Delete(_remoteRoot + file[_root.Length..]);
                    } catch(Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message} when deleting {file} as file, trying as folder");
                        try
                        {
                            Directory.Delete(_remoteRoot + file[_root.Length..]);
                        } catch(Exception ex2) {
                            Console.WriteLine($"Error: {ex2.Message} when deleting {file} as folder");
                        }
                    }
                })).ToList();

            await Task.WhenAll(deleteFileTasks.Union(createFileTasks));

            watch.Stop();


            Console.WriteLine();
            Console.WriteLine($"Done in {watch.Elapsed}");
            Console.WriteLine();

            onProbation = false;
        }

        private static void SetHandled(FileEvent fileEvent) => fileEvent.Handled = true;
        

        private static ConcurrentBag<FileEvent> UpdateEvents = new();
        private static ConcurrentBag<FileEvent> AlternateUpdateEvents = new();

        //if we have an error, then it's on probation - it'll try the events again and flush them if we see another error
        //allows us to retry in case it was a network error, but also flush events in case it was one of the events that caused the error
        private static bool  onProbation = false;

        private static void Watcher_Renamed(object sender, RenamedEventArgs e)
        {
            var now = DateTimeOffset.Now;
            while (clearingCollections)
            {
                Task.Delay(5).Wait();
            }
            UpdateEvents.Add(new FileEvent(false, now, e));
            Console.WriteLine($" -- {e.ChangeType}: {e.OldFullPath} to {e.FullPath}");
        }

        private static void Watcher_Update(object sender, FileSystemEventArgs e)
        {
            if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath)) return;
            var now = DateTimeOffset.Now;
            while (clearingCollections)
            {
                Task.Delay(5).Wait();
            }
            UpdateEvents.Add(new FileEvent(false, now, e));
            Console.WriteLine($" -- {e.ChangeType}: {e.FullPath}");
        }
        
        
        
        
        public static string GetPassword()
        {
            var pwd = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo i = Console.ReadKey(true);
                if (i.Key == ConsoleKey.Enter)
                {
                    break;
                }
                else if (i.Key == ConsoleKey.Backspace)
                {
                    if (pwd.Length > 0)
                    {
                        pwd.Remove(pwd.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                }
                else if (i.KeyChar != '\u0000' ) // KeyChar == '\u0000' if the key pressed does not correspond to a printable character, e.g. F1, Pause-Break, etc
                {
                    pwd.Append(i.KeyChar);
                    Console.Write("*");
                }
            }
            Console.WriteLine();
            return pwd.ToString();
        }
        

    }

    internal class FileEvent
    {
        public bool Handled { get; set; }
        public DateTimeOffset Time { get; }
        public FileSystemEventArgs Event { get; }
        public RenamedEventArgs RnEvent { get; }
        public bool IsRename => RnEvent != null;
        public FileEvent(bool handled, DateTimeOffset time, FileSystemEventArgs fileEvent)
        {
            Handled = handled;
            Time = time;
            Event = fileEvent;
        }
        public FileEvent(bool handled, DateTimeOffset time, RenamedEventArgs fileEvent)
        {
            Handled = handled;
            Time = time;
            RnEvent = fileEvent;
        }
    }
    internal class ConnectionSettings
    {
        public string Root { get; set; }
        public string RemoteRoot { get; set; }
        public string[] Folders { get; set; }

    }
}
