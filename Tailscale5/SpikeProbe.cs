using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Tailscale
{
    // Spike step C — "why does mmap fail?"
    //
    // The softfp/glibc-2.24 build cleared the loader's ELF/ABI check (the old
    // "internal error" is gone), but dlopen on the RU7020 now fails with
    // "failed to map segment from shared object": an mmap() inside the dynamic
    // loader was refused. glibc 2.24 drops the errno from that message, so this
    // class reproduces the loader's mmaps by hand (file R, file R+X, whole-span
    // R+X, big anonymous RW, RWX) and reports errno, together with the context
    // that decides such things on Tizen: mount options (noexec), the process
    // Smack label vs the file labels, RLIMIT_AS, overcommit, and the largest
    // free hole in the 32-bit address space. Tiny control libs (C, Go) separate
    // "policy refuses app-bundled native code" from "this 57MB Go image is too
    // big or laid out wrong". Everything goes to the :8081 diag (no sdb/dlog on
    // retail sets).
    static class SpikeProbe
    {
        [DllImport("libc", SetLastError = true)] static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] static extern IntPtr mmap(IntPtr addr, UIntPtr len, int prot, int flags, int fd, IntPtr off);
        [DllImport("libc", SetLastError = true)] static extern int munmap(IntPtr addr, UIntPtr len);
        [DllImport("libc", SetLastError = true)] static extern int mprotect(IntPtr addr, UIntPtr len, int prot);
        [DllImport("libc", SetLastError = true)] static extern IntPtr getxattr(string path, string name, byte[] value, UIntPtr size);
        [DllImport("libc")] static extern IntPtr strerror(int errnum);

        [DllImport("libdl.so.2", SetLastError = true)] static extern IntPtr dlopen(string filename, int flags);
        [DllImport("libdl.so.2")] static extern IntPtr dlsym(IntPtr handle, string symbol);
        [DllImport("libdl.so.2")] static extern IntPtr dlerror();
        [DllImport("libdl.so.2")] static extern int dlclose(IntPtr handle);

        const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;
        const int MAP_PRIVATE = 2, MAP_ANONYMOUS = 0x20;
        const int RTLD_NOW = 2, RTLD_GLOBAL = 0x100;
        static readonly IntPtr MAP_FAILED = new IntPtr(-1);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HelloCFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr HelloGoFn();

        // ---- context --------------------------------------------------------

        public static void DumpEnvironment(Action<string> log, params string[] paths)
        {
            log("PROBE smack label: " + ReadFirstLine("/proc/self/attr/current"));
            log("PROBE status: " + Grep("/proc/self/status", "Seccomp", "NoNewPrivs", "Uid:", "Gid:"));
            log("PROBE overcommit_memory=" + ReadFirstLine("/proc/sys/vm/overcommit_memory")
                + " | " + Grep("/proc/meminfo", "MemTotal", "MemAvailable", "CommitLimit", "Committed_AS"));
            log("PROBE limits: " + Grep("/proc/self/limits", "address space", "data seg", "locked memory"));
            foreach (var m in Mounts(paths)) log("PROBE mount: " + m);
            log("PROBE maps: " + MapsSummary());
            foreach (var p in paths)
            {
                log("PROBE xattr " + p + " -> " + Xattr(p));
                try
                {
                    string dir = Path.GetDirectoryName(p);
                    log("PROBE xattr " + dir + " -> " + Xattr(dir));
                }
                catch { }
            }
        }

        static string ReadFirstLine(string path)
        {
            try
            {
                using (var sr = new StreamReader(path))
                    return (sr.ReadLine() ?? "").Trim();
            }
            catch (Exception ex) { return "(unreadable: " + ex.GetType().Name + ")"; }
        }

        static string Grep(string path, params string[] keys)
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    foreach (var k in keys)
                        if (line.StartsWith(k, StringComparison.Ordinal))
                        {
                            if (sb.Length > 0) sb.Append("; ");
                            sb.Append(Collapse(line));
                            break;
                        }
                }
            }
            catch (Exception ex) { return "(unreadable " + path + ": " + ex.GetType().Name + ")"; }
            return sb.Length == 0 ? "(no matches in " + path + ")" : sb.ToString();
        }

        static string Collapse(string s)
        {
            var sb = new StringBuilder();
            bool ws = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { if (!ws) sb.Append(' '); ws = true; }
                else { sb.Append(c); ws = false; }
            }
            return sb.ToString();
        }

        // Mount entries whose mountpoint is a prefix of one of the paths (deepest
        // first), plus anything mounted under /opt. Fields: mountpoint fstype opts.
        static List<string> Mounts(string[] paths)
        {
            var result = new List<string>();
            string[] lines;
            try { lines = File.ReadAllLines("/proc/self/mounts"); }
            catch (Exception ex) { result.Add("(unreadable /proc/self/mounts: " + ex.GetType().Name + ")"); return result; }

            var seen = new HashSet<string>();
            foreach (var p in paths)
            {
                string best = null; string[] bestF = null;
                foreach (var l in lines)
                {
                    var f = l.Split(' ');
                    if (f.Length < 4) continue;
                    string mp = f[1] == "/" ? "/" : f[1].TrimEnd('/') + "/";
                    if (p.StartsWith(mp, StringComparison.Ordinal) && (best == null || mp.Length > best.Length)) { best = mp; bestF = f; }
                }
                if (bestF != null && seen.Add(bestF[1] + "|" + p))
                    result.Add("for " + p + ": " + bestF[1] + " " + bestF[2] + " " + bestF[3]);
            }
            foreach (var l in lines)
            {
                var f = l.Split(' ');
                if (f.Length >= 4 && f[1].StartsWith("/opt", StringComparison.Ordinal) && seen.Add(f[1]))
                    result.Add(f[1] + " " + f[2] + " " + f[3]);
            }
            return result;
        }

        // 32-bit process: how much is mapped, and how big is the largest free
        // hole below the 3GB user/kernel split. The loader needs one contiguous
        // hole of ~57MB for libtsspike.so (text 20.6MB + RW 36.5MB span).
        static string MapsSummary()
        {
            try
            {
                var ranges = new List<KeyValuePair<ulong, ulong>>();
                foreach (var l in File.ReadAllLines("/proc/self/maps"))
                {
                    int sp = l.IndexOf(' ');
                    if (sp < 0) continue;
                    var r = l.Substring(0, sp).Split('-');
                    ranges.Add(new KeyValuePair<ulong, ulong>(Convert.ToUInt64(r[0], 16), Convert.ToUInt64(r[1], 16)));
                }
                ranges.Sort((a, b) => a.Key.CompareTo(b.Key));
                ulong mapped = 0, biggestHole = 0, holeAt = 0, prevEnd = 0x10000;
                foreach (var r in ranges)
                {
                    mapped += r.Value - r.Key;
                    if (r.Key > prevEnd && r.Key - prevEnd > biggestHole) { biggestHole = r.Key - prevEnd; holeAt = prevEnd; }
                    if (r.Value > prevEnd) prevEnd = r.Value;
                }
                ulong top = 0xC0000000;
                if (top > prevEnd && top - prevEnd > biggestHole) { biggestHole = top - prevEnd; holeAt = prevEnd; }
                return ranges.Count + " mappings, " + (mapped >> 20) + "MB mapped, largest free hole "
                    + (biggestHole >> 20) + "MB at 0x" + holeAt.ToString("x8") + ", highest mapping ends 0x" + prevEnd.ToString("x8");
            }
            catch (Exception ex) { return "(unreadable /proc/self/maps: " + ex.GetType().Name + ")"; }
        }

        static string Xattr(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return "(missing)";
            var buf = new byte[256];
            IntPtr n = getxattr(path, "security.SMACK64", buf, (UIntPtr)(uint)buf.Length);
            if (n.ToInt64() < 0) return "SMACK64=? (" + Err() + ")";
            string label = Encoding.ASCII.GetString(buf, 0, (int)n.ToInt64()).TrimEnd('\0');
            n = getxattr(path, "security.SMACK64EXEC", buf, (UIntPtr)(uint)buf.Length);
            string exec = n.ToInt64() < 0 ? "" : " SMACK64EXEC=" + Encoding.ASCII.GetString(buf, 0, (int)n.ToInt64()).TrimEnd('\0');
            n = getxattr(path, "security.SMACK64MMAP", buf, (UIntPtr)(uint)buf.Length);
            string mm = n.ToInt64() < 0 ? "" : " SMACK64MMAP=" + Encoding.ASCII.GetString(buf, 0, (int)n.ToInt64()).TrimEnd('\0');
            return "SMACK64=" + label + exec + mm;
        }

        // ---- mmap probes ----------------------------------------------------

        public static void AnonProbe(Action<string> log)
        {
            TryMap(log, "anon 64MB RW", (UIntPtr)(64u << 20), PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1);
            TryMap(log, "anon 128MB RW", (UIntPtr)(128u << 20), PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1);
            TryMap(log, "anon 4K RWX", (UIntPtr)4096u, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANONYMOUS, -1);
            // JIT-style: RW page flipped to RX. CoreCLR itself needs this, so a
            // failure here would be surprising and worth knowing.
            IntPtr p = mmap(IntPtr.Zero, (UIntPtr)4096u, PROT_READ | PROT_WRITE, MAP_PRIVATE | MAP_ANONYMOUS, -1, IntPtr.Zero);
            if (p == MAP_FAILED) { log("PROBE mmap anon 4K RW for mprotect: FAILED " + Err()); return; }
            int rc = mprotect(p, (UIntPtr)4096u, PROT_READ | PROT_EXEC);
            log("PROBE mprotect anon RW->RX: " + (rc == 0 ? "OK" : "FAILED " + Err()));
            munmap(p, (UIntPtr)4096u);
        }

        public static void MapProbe(Action<string> log, string path)
        {
            int fd = open(path, 0 /* O_RDONLY */);
            if (fd < 0) { log("PROBE open(" + path + ") FAILED " + Err()); return; }
            try
            {
                long size = new FileInfo(path).Length;
                log("PROBE file " + path + " size=" + size);
                TryMap(log, "file 4K R", (UIntPtr)4096u, PROT_READ, MAP_PRIVATE, fd);
                TryMap(log, "file 4K R+X", (UIntPtr)4096u, PROT_READ | PROT_EXEC, MAP_PRIVATE, fd);
                TryMap(log, "file whole R+X", (UIntPtr)(ulong)size, PROT_READ | PROT_EXEC, MAP_PRIVATE, fd);
                // The loader's first mmap covers the whole PT_LOAD span (file +
                // bss), so it maps past EOF. Allowed for MAP_PRIVATE.
                TryMap(log, "file 64MB span R+X (past EOF)", (UIntPtr)(64u << 20), PROT_READ | PROT_EXEC, MAP_PRIVATE, fd);
                TryMap(log, "file 4K RW private", (UIntPtr)4096u, PROT_READ | PROT_WRITE, MAP_PRIVATE, fd);
            }
            finally { close(fd); }
        }

        static void TryMap(Action<string> log, string what, UIntPtr len, int prot, int flags, int fd)
        {
            IntPtr p = mmap(IntPtr.Zero, len, prot, flags, fd, IntPtr.Zero);
            if (p == MAP_FAILED) { log("PROBE mmap " + what + ": FAILED " + Err()); return; }
            log("PROBE mmap " + what + ": OK at 0x" + p.ToInt64().ToString("x8"));
            munmap(p, len);
        }

        static string Err()
        {
            int e = Marshal.GetLastWin32Error();
            return "errno=" + e + " " + (Marshal.PtrToStringAnsi(strerror(e)) ?? "?");
        }

        // ---- dlopen with real diagnostics -----------------------------------

        public static IntPtr Dlopen(Action<string> log, string path)
        {
            bool bare = path.IndexOf('/') < 0;
            if (!bare && !File.Exists(path)) { log("dlopen: not present " + path); return IntPtr.Zero; }
            dlerror(); // clear
            IntPtr h = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
            int errno = Marshal.GetLastWin32Error();
            if (h != IntPtr.Zero) { log("dlopen OK " + path); return h; }
            IntPtr e = dlerror();
            string msg = e == IntPtr.Zero ? "(no dlerror)" : (Marshal.PtrToStringAnsi(e) ?? "(null)");
            log("dlopen FAILED " + path + " -> " + msg
                + " [errno=" + errno + " " + (Marshal.PtrToStringAnsi(strerror(errno)) ?? "?") + "]"
                + (e == IntPtr.Zero ? "" : " raw=" + Hex(e, 48)));
            return IntPtr.Zero;
        }

        // Raw bytes of the dlerror buffer: the first run showed a single U+FFFD,
        // i.e. the string was not valid text. See what is really there.
        static string Hex(IntPtr p, int max)
        {
            var sb = new StringBuilder();
            try
            {
                for (int i = 0; i < max; i++)
                {
                    byte b = Marshal.ReadByte(p, i);
                    sb.Append(b.ToString("x2"));
                    if (b == 0) break;
                }
            }
            catch (Exception ex) { sb.Append("(" + ex.GetType().Name + ")"); }
            return sb.ToString();
        }

        public static void CallHelloC(Action<string> log, IntPtr h, bool closeAfter)
        {
            IntPtr s = dlsym(h, "hello_c");
            if (s == IntPtr.Zero) { log("hello_c: dlsym failed " + (Marshal.PtrToStringAnsi(dlerror()) ?? "?")); return; }
            try
            {
                var fn = Marshal.GetDelegateForFunctionPointer<HelloCFn>(s);
                log("hello_c() = " + fn() + " (expect 42) — in-process C code runs from this location");
            }
            catch (Exception ex) { log("hello_c invoke FAILED: " + ex.Message); }
            if (closeAfter) dlclose(h);
        }

        public static void CallHelloGo(Action<string> log, IntPtr h)
        {
            IntPtr s = dlsym(h, "HelloGo");
            if (s == IntPtr.Zero) { log("HelloGo: dlsym failed " + (Marshal.PtrToStringAnsi(dlerror()) ?? "?")); return; }
            try
            {
                var fn = Marshal.GetDelegateForFunctionPointer<HelloGoFn>(s);
                log("HelloGo() = " + (Marshal.PtrToStringAnsi(fn()) ?? "(null)") + " — a Go runtime started in-process (static TLS fits)");
            }
            catch (Exception ex) { log("HelloGo invoke FAILED: " + ex.Message); }
            // Never dlclose a Go c-shared (NODELETE, runtime can't unload).
        }
    }
}
