using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO.Enumeration;
using System.Runtime.InteropServices;

namespace OwkCopy;

public sealed record FileItem(string Src, string Dst, long Size);

/// <summary>
/// Multi-threaded copy/move engine. Never blocks waiting for user input: conflicts are
/// resolved by a policy chosen up front, errors are retried then logged, and the job continues.
/// </summary>
public sealed class CopyEngine
{
    const long LargeFileThreshold = 64L * 1024 * 1024;
    const int Attempts = 3;

    public event Action<string>? Info;
    public event Action<string>? Error;

    // Counters are read by the UI timer; written with Interlocked by workers.
    public long TotalBytes, DoneBytes;
    public int TotalFiles, DoneFiles, SkippedFiles, FailedFiles;
    public volatile string Phase = "Idle";
    public volatile string Current = "";

    public ConcurrentBag<FileItem> FailedItems { get; } = new();

    readonly ManualResetEventSlim _gate = new(true);
    readonly CancellationTokenSource _cts = new();
    readonly Settings _opt;
    readonly bool _move;
    string[] _excludes = [];

    public CopyEngine(Settings options, bool move)
    {
        _opt = options;
        _move = move;
        _excludes = options.Excludes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool Paused
    {
        get => !_gate.IsSet;
        set { if (value) _gate.Reset(); else _gate.Set(); }
    }

    public bool Cancelled => _cts.IsCancellationRequested;

    public void Cancel()
    {
        _cts.Cancel();
        _gate.Set();
    }

    // ---------------------------------------------------------------- planning

    sealed class Plan
    {
        public readonly List<FileItem> Files = new();
        public readonly List<string> DestDirs = new();
        public readonly List<string> SourceDirs = new();
    }

    public Task RunAsync(IReadOnlyList<string> sources, string destination) => Task.Run(() =>
    {
        Phase = "Scanning";
        var plan = new Plan();
        var dest = Path.GetFullPath(destination);
        Directory.CreateDirectory(dest);

        foreach (var raw in sources)
        {
            if (Cancelled) break;
            try { PlanSource(raw, dest, plan); }
            catch (Exception ex) { Fail(raw, ex.Message); }
        }

        Interlocked.Add(ref TotalFiles, plan.Files.Count);
        Interlocked.Add(ref TotalBytes, plan.Files.Sum(f => f.Size));

        Phase = "Creating folders";
        foreach (var d in plan.DestDirs)
        {
            if (Cancelled) break;
            try { Directory.CreateDirectory(d); }
            catch (Exception ex) { Fail(d, ex.Message); }
        }

        Execute(plan.Files);

        if (_move && !Cancelled)
        {
            Phase = "Cleaning up";
            // Deepest first so parents are empty by the time we reach them.
            foreach (var d in plan.SourceDirs.OrderByDescending(p => p.Length))
            {
                try { Directory.Delete(d, false); }
                catch { /* not empty: something was skipped or failed, keep it */ }
            }
        }

        Phase = Cancelled ? "Cancelled" : "Done";
        Current = "";
    });

    public Task RetryAsync(IReadOnlyList<FileItem> items) => Task.Run(() =>
    {
        Interlocked.Add(ref FailedFiles, -items.Count);
        Interlocked.Add(ref DoneBytes, -items.Sum(i => i.Size));
        FailedItems.Clear();
        Execute(items);
        Phase = Cancelled ? "Cancelled" : "Done";
        Current = "";
    });

    void PlanSource(string raw, string dest, Plan plan)
    {
        var src = Path.GetFullPath(raw);
        if (src.Length > 3) src = src.TrimEnd('\\');

        var name = Path.GetFileName(src);
        if (string.IsNullOrEmpty(name)) name = src[0] + "_drive";
        var target = Path.Combine(dest, name);

        if (IsSameOrInside(dest, src))
        {
            Fail(src, "Destination is inside the source folder.");
            return;
        }

        var attr = File.GetAttributes(src);
        bool isDir = attr.HasFlag(FileAttributes.Directory);

        if (PathEquals(src, target))
        {
            if (_move) { Info?.Invoke($"Already here, nothing to move: {src}"); return; }
            target = UniqueName(target, isDir);
        }

        if (isDir && attr.HasFlag(FileAttributes.ReparsePoint) && _opt.SkipLinks)
        {
            Skip(src, "link/junction");
            return;
        }

        // Same-volume move: a rename is instant regardless of size.
        if (_move && SameVolume(src, dest) && !Path.Exists(target))
        {
            try
            {
                if (isDir) Directory.Move(src, target); else File.Move(src, target);
                Info?.Invoke($"Moved (rename): {src}");
                return;
            }
            catch (Exception ex) { Info?.Invoke($"Rename failed, copying instead: {ex.Message}"); }
        }

        if (!isDir)
        {
            plan.Files.Add(new FileItem(src, target, new FileInfo(src).Length));
            return;
        }

        plan.DestDirs.Add(target);
        plan.SourceDirs.Add(src);
        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };

        var stack = new Stack<(string Src, string Dst)>();
        stack.Push((src, target));
        int scanned = 0;
        while (stack.Count > 0 && !Cancelled)
        {
            var (s, d) = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(s).EnumerateFileSystemInfos("*", enumOpts).ToList(); }
            catch (Exception ex) { Fail(s, ex.Message); continue; }

            foreach (var e in entries)
            {
                if (IsExcluded(e.Name)) continue;
                var dp = Path.Combine(d, e.Name);
                if (e.Attributes.HasFlag(FileAttributes.ReparsePoint) && _opt.SkipLinks)
                {
                    Skip(e.FullName, "link/junction");
                    continue;
                }
                if (e is DirectoryInfo)
                {
                    plan.DestDirs.Add(dp);
                    plan.SourceDirs.Add(e.FullName);
                    stack.Push((e.FullName, dp));
                }
                else
                {
                    plan.Files.Add(new FileItem(e.FullName, dp, ((FileInfo)e).Length));
                }
                if (++scanned % 2000 == 0) Current = $"{scanned:N0} items found";
            }
        }
    }

    // ---------------------------------------------------------------- execution

    void Execute(IReadOnlyList<FileItem> files)
    {
        if (Cancelled) return;
        Phase = _move ? "Moving" : "Copying";

        // Big files go through one sequential unbuffered lane (best for any disk);
        // small files are spread across parallel workers to hide per-file overhead.
        var large = new ConcurrentQueue<FileItem>(files.Where(f => f.Size >= LargeFileThreshold));
        var small = new ConcurrentQueue<FileItem>(files.Where(f => f.Size < LargeFileThreshold));
        int smallWorkers = Math.Max(1, Math.Clamp(_opt.Threads, 1, 64) - 1);

        var tasks = new List<Task>
        {
            Task.Factory.StartNew(() =>
            {
                while (!Cancelled && (large.TryDequeue(out var it) || small.TryDequeue(out it))) Process(it, true);
            }, TaskCreationOptions.LongRunning)
        };
        for (int i = 0; i < smallWorkers; i++)
            tasks.Add(Task.Factory.StartNew(() =>
            {
                while (!Cancelled && small.TryDequeue(out var it)) Process(it, false);
            }, TaskCreationOptions.LongRunning));

        Task.WaitAll(tasks.ToArray());
    }

    void Process(FileItem item, bool largeLane)
    {
        Exception? last = null;
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                _gate.Wait(_cts.Token);
            }
            catch (OperationCanceledException) { return; }

            long counted = 0;
            try
            {
                Current = item.Src;
                var dst = item.Dst;

                if (File.Exists(dst))
                {
                    switch (_opt.Conflict)
                    {
                        case ConflictPolicy.Skip:
                            SkipDone(item); return;
                        case ConflictPolicy.IfNewer:
                            var s = new FileInfo(item.Src);
                            var d = new FileInfo(dst);
                            if (s.Length == d.Length && s.LastWriteTimeUtc <= d.LastWriteTimeUtc.AddSeconds(2))
                            {
                                SkipDone(item); return;
                            }
                            ClearReadOnly(dst);
                            break;
                        case ConflictPolicy.Overwrite:
                            ClearReadOnly(dst);
                            break;
                        case ConflictPolicy.Rename:
                            dst = UniqueName(dst, false);
                            break;
                    }
                }

                if (_move && SameVolume(item.Src, dst))
                {
                    File.Move(item.Src, dst, true);
                    Interlocked.Add(ref DoneBytes, item.Size);
                    Interlocked.Increment(ref DoneFiles);
                    return;
                }

                var token = _cts.Token;
                Native.CopyProgressRoutine progress = (_, transferred, _, _, _, _, _, _, _) =>
                {
                    long delta = transferred - counted;
                    if (delta != 0) { Interlocked.Add(ref DoneBytes, delta); counted = transferred; }
                    while (!_gate.IsSet && !token.IsCancellationRequested) _gate.Wait(200);
                    return token.IsCancellationRequested ? Native.PROGRESS_CANCEL : Native.PROGRESS_CONTINUE;
                };

                uint flags = Native.COPY_FILE_ALLOW_DECRYPTED_DESTINATION;
                if (largeLane && item.Size >= LargeFileThreshold) flags |= Native.COPY_FILE_NO_BUFFERING;

                bool ok = Native.CopyFileEx(Native.LongPath(item.Src), Native.LongPath(dst), progress, IntPtr.Zero, IntPtr.Zero, flags);
                int err = Marshal.GetLastWin32Error();
                GC.KeepAlive(progress);
                if (!ok)
                {
                    if (err == Native.ERROR_REQUEST_ABORTED && Cancelled)
                    {
                        Interlocked.Add(ref DoneBytes, -counted);
                        return;
                    }
                    throw new Win32Exception(err);
                }

                if (_opt.Verify && !SameContent(item.Src, dst))
                    throw new IOException("Verification failed: contents differ.");

                if (_move)
                {
                    ClearReadOnly(item.Src);
                    File.Delete(item.Src);
                }

                Interlocked.Add(ref DoneBytes, item.Size - counted);
                Interlocked.Increment(ref DoneFiles);
                return;
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref DoneBytes, -counted);
                last = ex;
                if (attempt < Attempts) Thread.Sleep(300 * attempt);
            }
        }

        // Count the bytes as processed so progress still reaches 100%.
        Interlocked.Add(ref DoneBytes, item.Size);
        Interlocked.Increment(ref FailedFiles);
        FailedItems.Add(item);
        Error?.Invoke($"{item.Src}: {last?.Message}");
    }

    // ---------------------------------------------------------------- helpers

    void SkipDone(FileItem item)
    {
        Interlocked.Add(ref DoneBytes, item.Size);
        Interlocked.Increment(ref SkippedFiles);
    }

    void Skip(string path, string why)
    {
        Interlocked.Increment(ref SkippedFiles);
        Info?.Invoke($"Skipped {why}: {path}");
    }

    void Fail(string path, string message)
    {
        Interlocked.Increment(ref FailedFiles);
        Error?.Invoke($"{path}: {message}");
    }

    bool IsExcluded(string name)
    {
        foreach (var pattern in _excludes)
            if (FileSystemName.MatchesSimpleExpression(pattern, name)) return true;
        return false;
    }

    static void ClearReadOnly(string path)
    {
        var a = File.GetAttributes(path);
        if (a.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
    }

    static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool IsSameOrInside(string path, string folder)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    static string UniqueName(string path, bool isDir)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!Path.Exists(candidate)) return candidate;
        }
    }

    static bool SameContent(string a, string b)
    {
        const int buf = 1 << 20;
        using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buf, FileOptions.SequentialScan);
        using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buf, FileOptions.SequentialScan);
        if (fa.Length != fb.Length) return false;
        var x = new byte[buf];
        var y = new byte[buf];
        int n;
        while ((n = fa.ReadAtLeast(x, buf, false)) > 0)
        {
            if (fb.ReadAtLeast(y, n, false) != n) return false;
            if (!x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, n))) return false;
        }
        return true;
    }
}
