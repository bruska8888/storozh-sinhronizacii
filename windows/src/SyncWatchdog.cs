// Sync Watchdog for Windows 10/11. C# 5 / .NET Framework 4.x.
// No Explorer interaction: UI Automation reads existing tray surfaces only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

// File properties: Windows shows FileDescription as the notification source and in Task Manager,
// instead of the exe file name (which is "SyncWatchdog.exe.old" for a moment during an update).
[assembly: System.Reflection.AssemblyTitle("Sync Watchdog")]
[assembly: System.Reflection.AssemblyProduct("Sync Watchdog")]
[assembly: System.Reflection.AssemblyDescription("Keeps OneDrive and Dropbox running")]
[assembly: System.Reflection.AssemblyCopyright("MIT License")]
[assembly: System.Reflection.AssemblyVersion("1.6.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.6.0.0")]

namespace SyncWatchdog
{
    public sealed class ClientState
    {
        public string prev = "";
        public int cnt = 0;
        public bool doneRestart = false;
        public bool warned = false;
        public int netWaits = 0;      // restart postponed because the client was doing I/O (1.6)
        public int noIcon = 0;        // checks in a row without a tray icon (1.6)
    }

    public sealed class Settings
    {
        public bool OneDrive = true;
        public bool Dropbox = true;
        public bool AlwaysShowIcons = true;
        public bool AutoUpdate = true;
        public bool Watches(string name) { return name == "OneDrive" ? OneDrive : Dropbox; }
    }

    public sealed class UpdateState
    {
        public string lastCheck = "";
        public string latest = "";
        public string notified = "";
    }

    internal sealed class ReleaseInfo
    {
        public Version Version;
        public string ExeUrl;
        public string SigUrl;
    }

    internal sealed class TrayEntry
    {
        public string Surface;
        public string Name;
        public string ClassName;
        public string Type;
        public string Where;
    }

    // Graceful close through the Windows Restart Manager (rstrtmgr.dll), no force flag.
    internal static class RestartManager
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess
        {
            public int ProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
        }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint handle, int flags, string key);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint handle);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(uint handle, uint files, string[] fileNames, uint apps, UniqueProcess[] processes, uint services, string[] serviceNames);
        [DllImport("rstrtmgr.dll")]
        private static extern int RmShutdown(uint handle, uint flags, IntPtr callback);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicInfo
        {
            public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId;
        }
        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref BasicInfo info, int size, out int returned);

        internal static int ParentId(Process process)
        {
            try
            {
                BasicInfo info = new BasicInfo();
                int returned;
                if (NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf(info), out returned) != 0) return -1;
                return info.InheritedFromUniqueProcessId.ToInt32();
            }
            catch (Exception) { return -1; }
        }

        // Only root processes (parent is not the same client): registering Chromium helper processes
        // (crashpad, gpu, renderer) makes RmShutdown refuse to close anything (tested 2026-11-17).
        public static int Close(List<Process> processes)
        {
            HashSet<int> ids = new HashSet<int>();
            foreach (Process process in processes) ids.Add(process.Id);
            List<UniqueProcess> list = new List<UniqueProcess>();
            foreach (Process process in processes)
            {
                if (ids.Contains(ParentId(process))) continue;
                try
                {
                    long ft = process.StartTime.ToFileTime();
                    UniqueProcess item = new UniqueProcess();
                    item.ProcessId = process.Id;
                    item.StartTime.dwLowDateTime = (int)(ft & 0xFFFFFFFF);
                    item.StartTime.dwHighDateTime = (int)(ft >> 32);
                    list.Add(item);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            if (list.Count == 0) return 0;
            uint handle;
            int result = RmStartSession(out handle, 0, Guid.NewGuid().ToString("N"));
            if (result != 0) return result;
            try
            {
                result = RmRegisterResources(handle, 0, null, (uint)list.Count, list.ToArray(), 0, null);
                if (result != 0) return result;
                return RmShutdown(handle, 0, IntPtr.Zero);
            }
            finally { RmEndSession(handle); }
        }
    }

    internal sealed class CommandResult
    {
        public int Code = -1;
        public string Output = "";
    }

    internal static class Program
    {
        private const int Threshold = 4;
        private const string TaskName = "Sync Watchdog";
        private static readonly string[] Apps = { "OneDrive", "Dropbox" };
        private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), TaskName);
        private static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", TaskName);
        private static readonly string InstalledExe = Path.Combine(InstallDir, "SyncWatchdog.exe");
        private static readonly string Sid = WindowsIdentity.GetCurrent().User.Value;
        private static readonly int SessionId = Process.GetCurrentProcess().SessionId;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly List<string[]> Notifications = new List<string[]>();
        private static readonly Stopwatch CheckClock = new Stopwatch();
        private static readonly bool VerboseEnabled = Environment.GetEnvironmentVariable("STOROZH_VERBOSE") == "1";

        internal static string L(string ru, string en)
        {
            return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? ru : en;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            bool gui = args.Length == 0;
            // .NET 4.x defaults to old TLS; GitHub requires TLS 1.2 (3072).
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            // Test hook: STOROZH_LANG=en / ru overrides the Windows UI language (any non-ru language means English).
            string lang = Environment.GetEnvironmentVariable("STOROZH_LANG");
            if (!String.IsNullOrEmpty(lang))
            {
                try { Thread.CurrentThread.CurrentUICulture = new CultureInfo(lang); }
                catch (CultureNotFoundException) { }
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Directory.CreateDirectory(DataDir);
                if (gui)
                {
                    if (RelaunchInstalledCopy()) return 0;
                    // Separate from the check mutex: opening settings cannot skip a scheduled check.
                    using (Mutex mutex = new Mutex(false, "Local\\SyncWatchdog-UI-" + Sid))
                    {
                        if (!Acquire(mutex)) return 0;
                        try { InstallAndShow(); }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                else if (args.Length == 1 && args[0] == "--check")
                {
                    Verbose("--check start, session " + SessionId + ", culture " + CultureInfo.CurrentUICulture.Name);
                    using (Mutex mutex = new Mutex(false, "Local\\SyncWatchdog-Check-" + Sid))
                    {
                        if (!Acquire(mutex)) { Verbose("--check: another check is running, exit"); return 0; }
                        try { RunCheck(); ShowNotifications(); }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                else if (args.Length == 1 && args[0] == "--status") WriteStatus();
                else if (args.Length == 1 && args[0] == "--dump-tray") DumpTray();
                else if (args.Length == 1 && args[0] == "--uninstall")
                {
                    // Called by Settings → Apps → Installed apps → Uninstall (Windows asks for confirmation itself).
                    Uninstall();
                    MessageBox.Show(L("Сторож синхронизации удалён.", "Sync Watchdog has been removed."), L("Сторож синхронизации", "Sync Watchdog"));
                }
                else
                {
                    WriteDiagnostic("status.txt", L("Аргументы: --check, --status, --dump-tray", "Arguments: --check, --status, --dump-tray"));
                    return 2;
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { Log(L("Ошибка сторожа: ", "Watchdog error: ") + ex.Message); }
                catch { }
                if (gui) MessageBox.Show(ex.Message, L("Сторож синхронизации", "Sync Watchdog"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static bool Acquire(Mutex mutex)
        {
            try { return mutex.WaitOne(0); }
            catch (AbandonedMutexException) { return true; }
        }

        private static string DataPath(string name) { return Path.Combine(DataDir, name); }
        private static string Stamp() { return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        private static void Log(string message)
        {
            File.AppendAllText(DataPath("journal.log"), Stamp() + " — " + message + Environment.NewLine, Utf8);
            Verbose(message);
        }
        private static void Verbose(string message)
        {
            if (VerboseEnabled) File.AppendAllText(DataPath("verbose.txt"), Stamp() + " " + message + Environment.NewLine, Utf8);
        }

        // Same-directory replace: an interrupted write does not truncate saved settings/state.
        private static void WriteAtomic(string path, string text)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text, Utf8);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        private static T Load<T>(string file) where T : new()
        {
            if (!File.Exists(DataPath(file))) return new T();
            // Corrupt state/settings fail closed; do not silently enable clients or reset restart limits.
            T value = new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(DataPath(file), Utf8));
            if (value == null) throw new InvalidDataException(L("Некорректный JSON: ", "Invalid JSON: ") + file);
            return value;
        }
        private static void Save(string file, object value)
        {
            WriteAtomic(DataPath(file), new JavaScriptSerializer().Serialize(value));
        }
        private static void ResetState(string app) { Save(app + ".json", new ClientState()); }

        private static string FindClient(string app)
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates = app == "OneDrive"
                ? new string[] { Path.Combine(local, "Microsoft", "OneDrive", "OneDrive.exe"), Path.Combine(pf, "Microsoft OneDrive", "OneDrive.exe"), Path.Combine(pf86, "Microsoft OneDrive", "OneDrive.exe") }
                : new string[] { Path.Combine(pf86, "Dropbox", "Client", "Dropbox.exe"), Path.Combine(pf, "Dropbox", "Client", "Dropbox.exe"), Path.Combine(local, "Dropbox", "Client", "Dropbox.exe") };
            foreach (string candidate in candidates) if (File.Exists(candidate)) return candidate;
            if (app == "Dropbox")
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    string command = key == null ? null : key.GetValue("Dropbox") as string;
                    if (!String.IsNullOrWhiteSpace(command))
                    {
                        command = Environment.ExpandEnvironmentVariables(command.Trim());
                        Match match = Regex.Match(command, "^(?:\"([^\"]+\\.exe)\"|(.+?\\.exe))(?:\\s|$)", RegexOptions.IgnoreCase);
                        string candidate = match.Success ? (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : "";
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            return null;
        }
        private static List<Process> ClientProcesses(string app)
        {
            List<Process> result = new List<Process>();
            foreach (Process process in Process.GetProcessesByName(app))
            {
                try
                {
                    if (process.SessionId == SessionId && !process.HasExited) { result.Add(process); continue; }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            return result;
        }
        private static bool IsRunning(string app)
        {
            List<Process> processes = ClientProcesses(app);
            bool found = processes.Count != 0;
            foreach (Process process in processes) process.Dispose();
            return found;
        }
        private static bool Launch(string app, string path)
        {
            try
            {
                // "/systemstartup" is what Dropbox itself writes to HKCU\...\Run: starts silently in the tray.
                string arguments = app == "OneDrive" ? "/background" : "/systemstartup";
                // Launch through Explorer (Shell.Application), not as our child: the client must not live in the
                // scheduled task's process tree/job, which Task Scheduler may terminate at the 2-minute limit.
                Type shellType = Type.GetTypeFromProgID("Shell.Application");
                object shell = shellType == null ? null : Activator.CreateInstance(shellType);
                if (shell != null)
                {
                    try
                    {
                        shellType.InvokeMember("ShellExecute", System.Reflection.BindingFlags.InvokeMethod, null, shell,
                            new object[] { path, arguments, Path.GetDirectoryName(path), "open", 0 });
                        return true;
                    }
                    catch (Exception ex) { Verbose("Shell.Application: " + ex.Message); }
                    finally { Marshal.ReleaseComObject(shell); }
                }
                ProcessStartInfo info = new ProcessStartInfo(path, arguments);
                info.UseShellExecute = true;
                using (Process process = Process.Start(info)) { }
                return true;
            }
            catch (Exception ex)
            {
                Log(app + L(": не удалось запустить: ", ": could not launch: ") + ex.Message);
                return false;
            }
        }

        // Win32 argument quoting, including backslashes before quotes and at the end.
        private static string Quote(string value)
        {
            StringBuilder result = new StringBuilder();
            result.Append('"');
            int slashes = 0;
            foreach (char ch in value)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"')
                {
                    result.Append('\\', slashes * 2 + 1);
                    result.Append('"');
                }
                else
                {
                    result.Append('\\', slashes);
                    result.Append(ch);
                }
                slashes = 0;
            }
            result.Append('\\', slashes * 2);
            result.Append('"');
            return result.ToString();
        }
        private static CommandResult RunCommand(string path, string arguments, int timeout)
        {
            CommandResult result = new CommandResult();
            StringBuilder output = new StringBuilder();
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(path, arguments);
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                DataReceivedEventHandler collect = delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) lock (output) { output.AppendLine(e.Data); }
                };
                process.OutputDataReceived += collect;
                process.ErrorDataReceived += collect;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (process.WaitForExit(timeout))
                {
                    process.WaitForExit(); // drain asynchronous readers
                    result.Code = process.ExitCode;
                }
                else result.Code = -2;
                lock (output) { result.Output = output.ToString(); }
            }
            Verbose(path + " " + arguments + " => " + result.Code + " " + result.Output);
            return result;
        }
        private static string SystemExe(string name) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), name); }

        private static bool Restart(string app, string path)
        {
            if (!IsRunning(app)) return Launch(app, path);
            if (app == "OneDrive") RunCommand(path, "/shutdown", 5000);
            else
            {
                // Dropbox is an Electron tray app: WM_CLOSE / taskkill without /F only hides it (tested 2026-11-17).
                // Restart Manager asks it to quit the way Windows Update does (WM_QUERYENDSESSION + ENDSESSION_CLOSEAPP),
                // never forcing: without RmForceShutdown a refusing app simply stays running.
                List<Process> processes = ClientProcesses(app);
                try
                {
                    int code = RestartManager.Close(processes);
                    Verbose("RmShutdown " + app + " => " + code);
                }
                catch (Exception ex) { Verbose("RmShutdown: " + ex.Message); }
                finally { foreach (Process process in processes) process.Dispose(); }
            }
            Stopwatch waiting = Stopwatch.StartNew();
            // Poll fresh process objects, including replacement/child instances in this session.
            while (IsRunning(app) && waiting.Elapsed.TotalSeconds < 60 && CheckClock.Elapsed.TotalSeconds < 100)
                Thread.Sleep(500);
            if (IsRunning(app))
            {
                Log(app + L(": завис, штатно не закрылся — оставил как есть", ": hung and did not quit normally — left as is"));
                return false;
            }
            Thread.Sleep(3000);
            return Launch(app, path);
        }

        private static bool ProbeInternet(string url, string marker)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.AllowAutoRedirect = false;
                request.Method = "GET";
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                {
                    char[] buffer = new char[4096];
                    int length = reader.ReadBlock(buffer, 0, buffer.Length);
                    string body = new string(buffer, 0, length).Trim();
                    return response.StatusCode == HttpStatusCode.OK && (marker == "Microsoft Connect Test"
                        ? body == marker : body.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0);
                }
            }
            catch (Exception ex) { Verbose("internet: " + ex.Message); return false; }
        }
        private static bool InternetOK()
        {
            return ProbeInternet("http://www.msftconnecttest.com/connecttest.txt", "Microsoft Connect Test")
                || ProbeInternet("http://captive.apple.com", "<BODY>Success</BODY>");
        }

        private static string Normalize(string text) { return Regex.Replace(text ?? "", @"\s+", " ").Trim(); }
        private static bool ContainsAny(string text, string[] words)
        {
            foreach (string word in words) if (text.Contains(word)) return true;
            return false;
        }
        private enum Busy { No, Yes, Skip, Unknown }
        // OneDrive joins several lines, each starting with "OneDrive - <account>":
        // "OneDrive - Личная Выполнено … OneDrive - Личная Скачивание файлов (0%)". Judge every part; busy wins, then skip.
        // Unknown = no part matched any known word (a client update changed its wording) — never restarted, reported once (1.6).
        private static Busy Classify(string text)
        {
            string[] parts = Regex.Split(text ?? "", @"\s+(?=(?:OneDrive|Dropbox)\b)");
            bool skip = false, known = false;
            foreach (string part in parts)
            {
                Busy b = ClassifyPart(part);
                if (b == Busy.Yes) return Busy.Yes;
                if (b == Busy.Skip) skip = true;
                if (b != Busy.Unknown) known = true;
            }
            return skip ? Busy.Skip : (known ? Busy.No : Busy.Unknown);
        }
        private static Busy ClassifyPart(string text)
        {
            string s = text.ToLowerInvariant();
            if (ContainsAny(s, new string[] { "выполнено", "обновлено", "up to date", "files are up to date", "синхронизировано" })) return Busy.No;
            // Only the app name (and version) — the client is starting up
            if (Regex.IsMatch(s.Trim(), @"^(onedrive|dropbox)(\s+[\d.]+)?$")) return Busy.No;
            if (ContainsAny(s, new string[] { "приостановл", "пауз", "нет подключения", "не в сети", "ошибк", "требуется", "войдите", "paused", "offline", "not connected", "error", "sign in", "signed out" })) return Busy.Skip;
            if (ContainsAny(s, new string[] { "отправк", "скачиван", "поиск измен", "looking for changes", "обновление", "updating", "загрузк", "соединени", "подключени", "синхрониз", "обработк", "индекс", "подготовк", "проверк", "upload", "download", "connecting", "syncing", "processing", "indexing", "preparing" })) return Busy.Yes;
            return Busy.Unknown;
        }

        // Unknown statuses already reported (digits → #, so "(37%)" does not create new entries)
        private static string UnknownKey(string app, string status) { return app + ": " + Regex.Replace(status, @"\d", "#"); }
        private static bool UnknownReported(string key)
        {
            string path = DataPath("unknown-statuses.txt");
            return File.Exists(path) && Array.IndexOf(File.ReadAllLines(path, Utf8), key) >= 0;
        }
        private static void MarkUnknownReported(string key)
        {
            string path = DataPath("unknown-statuses.txt");
            List<string> lines = File.Exists(path) ? new List<string>(File.ReadAllLines(path, Utf8)) : new List<string>();
            lines.Add(key);
            if (lines.Count > 50) lines.RemoveRange(0, lines.Count - 50);
            WriteAtomic(path, String.Join(Environment.NewLine, lines.ToArray()) + Environment.NewLine);
        }

        // ---------- Is the client doing anything right now? (1.6) ----------
        // Second confirmation of a hang. Windows has no per-process network counters without admin rights, so use the
        // process I/O counters (disk + network): an uploading client reads the file and sends it; a hung one is idle.
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
        private const int NetPauseMs = 30000;              // pause between the two samples
        private const long NetMinBytes = 1024 * 1024;      // this much I/O during the pause = "working"
        private const int NetMaxWaits = 6;                 // postpone a restart for at most ~1 h

        private static Dictionary<int, long> ClientIo(string app)
        {
            Dictionary<int, long> result = new Dictionary<int, long>();
            foreach (Process process in ClientProcesses(app))
            {
                try
                {
                    IoCounters c;
                    if (GetProcessIoCounters(process.Handle, out c))
                        result[process.Id] = (long)(c.ReadTransferCount + c.WriteTransferCount + c.OtherTransferCount);
                }
                catch (Exception ex) { Verbose("io: " + ex.Message); }
                finally { process.Dispose(); }
            }
            return result;
        }
        // true = working, false = idle, null = could not tell
        private static bool? ClientActive(string app)
        {
            string fake = Environment.GetEnvironmentVariable("FAKE_NET_" + app);
            if (fake != null) return fake == "1";
            Dictionary<int, long> a = ClientIo(app);
            if (a.Count == 0) return null;
            System.Threading.Thread.Sleep(NetPauseMs);
            Dictionary<int, long> b = ClientIo(app);
            long moved = 0;
            foreach (KeyValuePair<int, long> kv in b)
            {
                long before;
                if (a.TryGetValue(kv.Key, out before)) moved += Math.Max(0, kv.Value - before);
            }
            Verbose(app + ": I/O over " + (NetPauseMs / 1000) + " s = " + moved + " bytes");
            return moved >= NetMinBytes;
        }

        private static List<TrayEntry> ReadTray()
        {
            List<TrayEntry> entries = new List<TrayEntry>();
            // Only existing top-level surfaces. Never expand/click the Win11 overflow button.
            Condition surfaces = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"));
            try
            {
                AutomationElementCollection windows = AutomationElement.RootElement.FindAll(TreeScope.Children, surfaces);
                foreach (AutomationElement window in windows)
                {
                    try
                    {
                        string surface = window.Current.ClassName;
                        // Read names regardless of control type: Explorer versions differ (Button/MenuItem/Custom).
                        AutomationElementCollection elements = window.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                        foreach (AutomationElement element in elements)
                        {
                            try
                            {
                                AutomationElement.AutomationElementInformation info = element.Current;
                                string name = Normalize(info.Name);
                                if (name.Length == 0) continue;
                                System.Windows.Rect r = info.BoundingRectangle;
                                string where = info.IsOffscreen || r.IsEmpty ? "offscreen" : String.Format(CultureInfo.InvariantCulture, "{0:0},{1:0} {2:0}x{3:0}", r.X, r.Y, r.Width, r.Height);
                                entries.Add(new TrayEntry { Surface = surface, Name = name, ClassName = info.ClassName, Type = info.ControlType.ProgrammaticName, Where = where });
                            }
                            catch (ElementNotAvailableException) { }
                        }
                    }
                    catch (ElementNotAvailableException) { }
                    catch (InvalidOperationException ex) { Verbose("UIA surface: " + ex.Message); }
                }
            }
            catch (Exception ex) { Verbose("UIA: " + ex.Message); }
            return entries;
        }
        private static string TrayStatus(List<TrayEntry> entries, string app)
        {
            // Preserve multiple OneDrive accounts in stable order; an active account must not be masked by an idle one.
            SortedSet<string> names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (TrayEntry entry in entries)
                if (entry.Name.StartsWith(app, StringComparison.OrdinalIgnoreCase)) names.Add(entry.Name);
            if (names.Count == 0) return null;
            List<string> busy = new List<string>();
            List<string> idle = new List<string>();
            foreach (string name in names)
            {
                if (Classify(name) == Busy.Yes) busy.Add(name);
                else idle.Add(name);
            }
            return String.Join(" | ", (busy.Count > 0 ? busy : idle).ToArray());
        }

        // The hidden overflow flyout does not exist in UIA until opened, and opening it would flash every 10 min.
        // Windows 11 does pick up HKCU\Control Panel\NotifyIconSettings\*\IsPromoted=1 live (checked 2026-11-17 on
        // Win11: the icon came back onto the taskbar within a check after being dragged under "^").
        private static void PromoteIcons(Settings settings)
        {
            if (!settings.AlwaysShowIcons) return;
            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true))
                {
                    if (root == null) return; // Older Windows; do not manufacture Explorer records.
                    foreach (string subkey in root.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey key = root.OpenSubKey(subkey, true))
                            {
                                if (key == null) continue;
                                string path = key.GetValue("ExecutablePath") as string;
                                if (String.IsNullOrWhiteSpace(path)) continue;
                                string exe = Path.GetFileName(Environment.ExpandEnvironmentVariables(path).Trim('"'));
                                bool known = String.Equals(exe, "OneDrive.exe", StringComparison.OrdinalIgnoreCase)
                                    || String.Equals(exe, "Dropbox.exe", StringComparison.OrdinalIgnoreCase);
                                if (known && !Object.Equals(key.GetValue("IsPromoted"), 1))
                                {
                                    key.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                                    Log(L("Значок возвращён на панель задач: ", "Tray icon moved back onto the taskbar: ") + exe);
                                }
                            }
                        }
                        catch (Exception ex) { Verbose("NotifyIconSettings: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { Verbose("NotifyIconSettings: " + ex.Message); }
        }
        private static string HiddenIconText()
        {
            return L("значок спрятан под стрелку «^» — зависание не видно", "icon is hidden under the “^” arrow — hangs cannot be detected");
        }


        private static void RunCheck()
        {
            CheckClock.Restart();
            List<string> report = new List<string>();
            try
            {
                Settings settings = Load<Settings>("settings.json");
                if (!File.Exists(DataPath("settings.json"))) Save("settings.json", settings);
                PromoteIcons(settings);
                List<TrayEntry> tray = null;
                bool? internet = null; // one bounded network probe per run, shared by both clients
                foreach (string app in Apps)
                {
                    try
                    {
                        string path = FindClient(app);
                        if (path == null)
                        {
                            report.Add(app + L(": не установлен", ": not installed"));
                            continue;
                        }
                        if (!settings.Watches(app))
                        {
                            report.Add(app + L(": наблюдение выключено", ": monitoring disabled"));
                            continue;
                        }
                        if (!IsRunning(app))
                        {
                            if (Launch(app, path))
                            {
                                Log(app + L(" не работал, запущен", " was not running, launched"));
                                report.Add(app + L(": не работал — запущен", ": was not running — launched"));
                                ResetState(app);
                            }
                            else report.Add(app + L(": не удалось запустить", ": launch failed"));
                            continue;
                        }
                        ClientState state = Load<ClientState>(app + ".json");
                        string status = Environment.GetEnvironmentVariable("FAKE_" + app);
                        if (status == null)
                        {
                            if (tray == null) tray = ReadTray();
                            status = TrayStatus(tray, app);
                        }
                        else status = Normalize(status);
                        if (String.IsNullOrEmpty(status) || status == "__noicon__")
                        {
                            if (state.prev != "__nostatus__") { Log(app + ": " + HiddenIconText()); state.noIcon = 0; }
                            else state.noIcon++;
                            // ~30 min without an icon: say once that hangs are not visible (1.6)
                            if (state.noIcon == 2)
                            {
                                Log(app + L(": значка нет ~30 мин — уведомил: зависания не видны", ": no icon for ~30 min — notified: hangs cannot be detected"));
                                Notifications.Add(new string[] { L("Сторож не видит значок ", "Sync Watchdog cannot see the icon of ") + app,
                                    L("Если " + app + " закроется, сторож его запустит, но зависание не заметит. Включите «Всегда показывать значки» в окне сторожа.",
                                      "If " + app + " quits it will be relaunched, but a hang will go unnoticed. Turn on “Always show icons” in the watchdog window.") });
                            }
                            // A missing observation is not a changed client text. Keep episode safety flags.
                            state.prev = "__nostatus__";
                            state.cnt = 0;
                            Save(app + ".json", state);
                            report.Add(app + ": " + HiddenIconText());
                            continue;
                        }
                        Busy busy = Classify(status);
                        if (busy == Busy.Unknown && status == state.prev)
                        {
                            // Same unknown text two checks in a row — report once per text (1.6)
                            string key = UnknownKey(app, status);
                            if (!UnknownReported(key))
                            {
                                Log(app + L(": незнакомый статус «", ": unknown status “") + status + L("» — не понимаю его, зависание с ним не замечу", "” — cannot tell whether it is a hang"));
                                Notifications.Add(new string[] { L("Сторож: незнакомый статус ", "Sync Watchdog: unknown status of ") + app,
                                    "«" + status + "». " + L("Возможно, " + app + " обновился. Пока статус непонятен, зависание не ловится — проверьте обновление сторожа.",
                                                            app + " may have been updated. Hangs are not detected with this status — check for a watchdog update.") });
                                MarkUnknownReported(key);
                            }
                        }
                        if (busy == Busy.Yes && status == state.prev) state.cnt = Math.Min(state.cnt + 1, 100000);
                        else
                        {
                            state.cnt = 0;
                            if (status != state.prev) { state.doneRestart = false; state.warned = false; state.netWaits = 0; }
                        }
                        string action = "";
                        if (busy == Busy.Yes && state.cnt >= Threshold && !(state.doneRestart && state.warned))
                        {
                            // Keep well within the task's 2-minute limit, leaving time to persist the report.
                            if (CheckClock.Elapsed.TotalSeconds >= 75)
                                action = L(", действие отложено до следующей проверки", ", action deferred until next check");
                            else
                            {
                                if (!internet.HasValue) internet = InternetOK();
                                if (!internet.Value) action = L(", нет интернета — не перезапускаю", ", no internet — not restarting");
                                // Text is stuck but the client is working (big file, slow link) — wait, at most ~1 h (1.6).
                                // The 30-s sample must fit the task's 2-minute limit together with a restart.
                                else if (!state.doneRestart && state.netWaits < NetMaxWaits && CheckClock.Elapsed.TotalSeconds >= 40)
                                    action = L(", действие отложено до следующей проверки", ", action deferred until next check");
                                else if (!state.doneRestart && state.netWaits < NetMaxWaits && ClientActive(app) == true)
                                {
                                    if (state.netWaits == 0)
                                        Log(app + L(": статус «", ": status “") + status + L("» стоит ~40 мин, но программа работает — жду", "” unchanged ~40 min, but the client is working — waiting"));
                                    state.netWaits++;
                                    action = L(", программа работает — жду", ", client is working — waiting");
                                }
                                else if (!state.doneRestart)
                                {
                                    state.netWaits = 0;
                                    // Persist the attempt BEFORE shutdown: interruption must not cause a restart loop.
                                    state.prev = status;
                                    state.doneRestart = true;
                                    state.cnt = 0;
                                    Save(app + ".json", state);
                                    Log(app + L(": статус «", ": status “") + status + L("» не менялся ~40 мин — перезапускаю", "” unchanged for ~40 min — restarting"));
                                    bool restarted = Restart(app, path);
                                    action = restarted ? L(", перезапущен", ", restarted") : L(", перезапуск не завершён — оставил как есть", ", restart incomplete — left as is");
                                }
                                else if (!state.warned)
                                {
                                    state.warned = true;
                                    state.prev = status;
                                    Save(app + ".json", state);
                                    Log(app + L(": после попытки перезапуска снова завис — нужен человек: ", ": stuck again after restart attempt — needs a human: ") + status);
                                    Notifications.Add(new string[] { app + L(" не синхронизируется", " is not syncing"), L("Снова завис после попытки перезапуска: ", "Stuck again after restart attempt: ") + status });
                                }
                            }
                        }
                        state.prev = status;
                        Save(app + ".json", state);
                        string summary = busy == Busy.Yes
                            ? (state.cnt > 0 ? L("занят, без изменений ~", "busy, unchanged for ~") + (state.cnt * 10) + L(" мин", " min") : L("занят, идёт работа", "busy, working"))
                            : (busy == Busy.Skip ? L("пауза/нет сети — не трогаю", "paused/offline — leaving alone")
                            : (busy == Busy.Unknown ? L("незнакомый статус — зависание не вижу", "unknown status — cannot detect hangs") : L("в порядке", "OK")));
                        report.Add(app + ": «" + status + "» — " + summary + action
                            + (state.doneRestart ? L(", попытка перезапуска уже была", ", restart already attempted") : "")
                            + (state.warned ? L(", ПРЕДУПРЕДИЛ", ", WARNED") : ""));
                        Verbose(app + ": " + status + " busy=" + busy + " cnt=" + state.cnt + " restart=" + state.doneRestart + " warned=" + state.warned);
                    }
                    catch (Exception ex)
                    {
                        report.Add(app + L(": ошибка проверки: ", ": check error: ") + ex.Message);
                        Log(app + L(": ошибка проверки: ", ": check error: ") + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                report.Add(L("Проверка не выполнена: ", "Check failed: ") + ex.Message);
                throw;
            }
            finally
            {
                try
                {
                    Settings updateSettings = Load<Settings>("settings.json");
                    string updated = CheckForUpdate(updateSettings.AutoUpdate, false);
                    if (updated != null) report.Add(updated);
                }
                catch (Exception ex) { Verbose("update: " + ex.Message); }
                // Unlike the Mac version, test runs also update last-check, as required by the Windows spec.
                string test = Environment.GetEnvironmentVariable("FAKE_OneDrive") != null || Environment.GetEnvironmentVariable("FAKE_Dropbox") != null
                    ? L(" [ТЕСТ FAKE]", " [FAKE TEST]") : "";
                WriteAtomic(DataPath("last-check.txt"), L("Последняя проверка: ", "Last check: ") + Stamp() + test + Environment.NewLine + String.Join(Environment.NewLine, report.ToArray()) + Environment.NewLine);
            }
        }

        // ---------- Updates from GitHub Releases ----------
        // Once a day (during a scheduled check) ask GitHub for the latest release. The new exe is accepted only if
        // its RSA-SHA256 signature (SyncWatchdog-<ver>.exe.sig in the same release) matches the public key below;
        // the private key is kept in Bitwarden ("Sync Watchdog — ключ подписи релизов"), see windows/sign-release.ps1.
        private const string LatestReleaseApi = "https://api.github.com/repos/bruska8888/storozh-sinhronizacii/releases/latest";
        private const string UpdatePublicKey = "<RSAKeyValue><Modulus>sOrH8bW6LdMEuUkT9a/INnECyCMzLrmMgffS/ScG52TlaHBBz86hvnCu5b3m/geWLXQWAGW125bXemwO/iUaAJTTOlVkQPzoJFC/fzQX3+7dbDzLCn5mVWBG52RcfOo4zDb3junbPQKqh2E9iVFe7SgRCN3Cj4zklHIFZWjqa8qq9wyplC+AeGMsJfeEObLfU14IxqCCbmSfzyYMFEqXVxWRohZyGiXy+bZY809lthlfhIitjJj3gTEKcJlFez6jXIhDWKbHI05yiyNqr94fQUJBuW1NViyJTDavFsumOnmHa8yaOY4WMDDGAB7EPSvNrLS3gHm0MqijqH3YnY+4g5AQhifC4Wof3Qx3Z8Z0sNT33Coci6+5VTfHgUdDzr7yds6mn8eJE11ZmFVYw+LmlMpDRsW6b+5ZOuypeetw5fGUCQ8UYaN22T4Di4gwaVwV+PIXFQ9cr4TNXVEdpN9SkNHB2beaBlM2SW30UmqwJ2EzrPWp1fcQVI+CQVFoAZ3x</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";

        private static byte[] HttpGet(string url, int maxBytes)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "SyncWatchdog/" + Version;
            request.Accept = url.Contains("api.github.com") ? "application/vnd.github+json" : "application/octet-stream";
            request.Timeout = 20000;
            request.ReadWriteTimeout = 20000;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[65536];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > maxBytes) throw new InvalidDataException("download too large");
                }
                return memory.ToArray();
            }
        }

        internal static ReleaseInfo LatestRelease()
        {
            string json = Encoding.UTF8.GetString(HttpGet(LatestReleaseApi, 1 << 20));
            Dictionary<string, object> release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            ReleaseInfo info = new ReleaseInfo();
            Dictionary<string, string> urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            object[] assets = release.ContainsKey("assets") ? release["assets"] as object[] : null;
            System.Collections.ArrayList list = release.ContainsKey("assets") ? release["assets"] as System.Collections.ArrayList : null;
            IEnumerable<object> items = assets != null ? (IEnumerable<object>)assets : (list != null ? list.ToArray() : new object[0]);
            foreach (object item in items)
            {
                Dictionary<string, object> asset = item as Dictionary<string, object>;
                if (asset == null) continue;
                urls[(string)asset["name"]] = (string)asset["browser_download_url"];
            }
            foreach (string name in urls.Keys)
            {
                Match match = Regex.Match(name, @"^SyncWatchdog-(\d+(?:\.\d+){1,3})\.exe$", RegexOptions.IgnoreCase);
                if (!match.Success || !urls.ContainsKey(name + ".sig")) continue;
                Version version = new Version(match.Groups[1].Value);
                if (info.Version == null || version > info.Version)
                {
                    info.Version = version;
                    info.ExeUrl = urls[name];
                    info.SigUrl = urls[name + ".sig"];
                }
            }
            return info.Version == null ? null : info;
        }

        internal static bool SignatureValid(byte[] data, byte[] signature)
        {
            using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(UpdatePublicKey);
                using (SHA256Managed sha = new SHA256Managed())
                    return rsa.VerifyHash(sha.ComputeHash(data), CryptoConfig.MapNameToOID("SHA256"), signature);
            }
        }

        private static Version CurrentVersion() { return new Version(Version); }

        // Downloads, verifies and installs the release. The running exe cannot be overwritten, but it can be renamed:
        // SyncWatchdog.exe -> SyncWatchdog.exe.old (removed on the next run), then the new file takes its place.
        internal static void InstallUpdate(ReleaseInfo release)
        {
            byte[] exe = HttpGet(release.ExeUrl, 20 << 20);
            byte[] sig = Convert.FromBase64String(Encoding.ASCII.GetString(HttpGet(release.SigUrl, 1 << 16)).Trim());
            if (!SignatureValid(exe, sig))
            {
                Log(L("Обновление ", "Update ") + release.Version + L(" не прошло проверку подписи — НЕ установлено", " failed the signature check — NOT installed"));
                throw new InvalidDataException(L("Подпись обновления неверна, обновление не установлено.", "The update signature is invalid; the update was not installed."));
            }
            string old = InstalledExe + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(InstalledExe, old);
            try { File.WriteAllBytes(InstalledExe, exe); }
            catch { File.Move(old, InstalledExe); throw; }
            RegisterApp(release.Version.ToString());
            Log(L("Сторож обновлён: ", "Watchdog updated: ") + Version + " → " + release.Version);
        }

        // Returns a line for last-check when something happened; null otherwise.
        internal static string CheckForUpdate(bool auto, bool force)
        {
            try { if (File.Exists(InstalledExe + ".old")) File.Delete(InstalledExe + ".old"); } catch (Exception) { }
            if (!String.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return null;
            UpdateState state = Load<UpdateState>("update.json");
            DateTime last;
            if (!force && DateTime.TryParse(state.lastCheck, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out last)
                && (DateTime.UtcNow - last).TotalHours < 20) return null;
            state.lastCheck = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            ReleaseInfo release = null;
            try { release = LatestRelease(); }
            catch (Exception ex) { Verbose("update check: " + ex.Message); }
            if (release == null || release.Version <= CurrentVersion())
            {
                state.latest = "";
                Save("update.json", state);
                return null;
            }
            state.latest = release.Version.ToString();
            string message;
            if (auto)
            {
                Save("update.json", state); // a failed install is retried tomorrow, not every 10 minutes
                InstallUpdate(release);
                Notifications.Add(new string[] { L("Сторож синхронизации обновлён", "Sync Watchdog updated"), L("Установлена версия ", "Installed version ") + release.Version, "info" });
                message = null; // the journal and the notification record it; last-check describes the clients only
            }
            else
            {
                if (state.notified != state.latest)
                {
                    Notifications.Add(new string[] { L("Есть новая версия сторожа: ", "New Sync Watchdog version: ") + release.Version,
                        L("Откройте «Сторож синхронизации» в меню «Пуск» и нажмите «Обновить».", "Open Sync Watchdog from the Start menu and click Update."), "info" });
                    state.notified = state.latest;
                }
                message = L("Доступна новая версия ", "New version available: ") + release.Version;
            }
            Save("update.json", state);
            return message;
        }

        internal static Icon AppIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
            catch (Exception) { return SystemIcons.Application; }
        }

        private static void ShowNotifications()
        {
            if (Notifications.Count == 0) return;
            using (NotifyIcon icon = new NotifyIcon())
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                icon.Icon = AppIcon();
                icon.Text = "Sync Watchdog";
                icon.Visible = true;
                List<string> titles = new List<string>();
                List<string> bodies = new List<string>();
                bool warning = false;
                foreach (string[] item in Notifications) { titles.Add(item[0]); bodies.Add(item[1]); if (item.Length < 3 || item[2] != "info") warning = true; }
                string title = String.Join("; ", titles.ToArray());
                string body = String.Join(Environment.NewLine, bodies.ToArray());
                if (title.Length > 63) title = title.Substring(0, 60) + "...";
                if (body.Length > 250) body = body.Substring(0, 247) + "...";
                timer.Interval = 11000;
                timer.Tick += delegate { timer.Stop(); Application.ExitThread(); };
                // Keep a message loop alive: disposing immediately would discard the balloon.
                icon.ShowBalloonTip(10000, title, body, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
                timer.Start();
                Application.Run();
                icon.Visible = false;
            }
        }

        // Installer window shown when the exe is run from anywhere except its installed location.
        private static bool AskInstall(bool upgrade)
        {
            using (Form form = new Form())
            {
                form.Text = L("Установка сторожа синхронизации ", "Sync Watchdog setup ") + Version;
                form.Icon = AppIcon();
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.TopMost = true;
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.AutoSize = true;
                form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                form.Font = new Font("Segoe UI", 10);
                TableLayoutPanel layout = new TableLayoutPanel();
                layout.AutoSize = true;
                layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                layout.Padding = new Padding(18);
                layout.ColumnCount = 1;
                form.Controls.Add(layout);
                Label title = new Label();
                title.AutoSize = true;
                title.Font = new Font("Segoe UI", 13, FontStyle.Bold);
                title.Text = upgrade ? L("Обновить сторожа синхронизации?", "Update Sync Watchdog?") : L("Установить сторожа синхронизации?", "Install Sync Watchdog?");
                layout.Controls.Add(title);
                Label text = new Label();
                text.AutoSize = true;
                text.MaximumSize = new Size(520, 0);
                text.Margin = new Padding(3, 10, 3, 0);
                text.Text = L("Сторож раз в 10 минут проверяет OneDrive и Dropbox и перезапускает их, если они закрылись или зависли. Работает в фоне, окон не показывает. Права администратора не нужны.\n\nПосле установки он появится в меню «Пуск», а удалить его можно в «Параметры → Приложения → Установленные приложения».",
                    "Every 10 minutes the watchdog checks OneDrive and Dropbox and restarts them if they quit or hang. It works in the background without windows. No administrator rights are needed.\n\nAfter setup it appears in the Start menu; uninstall it in Settings → Apps → Installed apps.");
                layout.Controls.Add(text);
                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.AutoSize = true;
                buttons.Anchor = AnchorStyles.Right;
                buttons.Margin = new Padding(0, 18, 0, 0);
                layout.Controls.Add(buttons);
                Button install = new Button();
                install.Text = upgrade ? L("Обновить", "Update") : L("Установить", "Install");
                install.AutoSize = true;
                install.DialogResult = DialogResult.OK;
                Button cancel = new Button();
                cancel.Text = L("Отмена", "Cancel");
                cancel.AutoSize = true;
                cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(install);
                buttons.Controls.Add(cancel);
                form.AcceptButton = install;
                form.CancelButton = cancel;
                form.ActiveControl = install;
                return form.ShowDialog() == DialogResult.OK;
            }
        }

        private static bool RelaunchInstalledCopy()
        {
            string current = Path.GetFullPath(Application.ExecutablePath);
            if (String.Equals(current, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return false;
            bool upgrade = File.Exists(InstalledExe);
            if (!AskInstall(upgrade)) return true;
            Directory.CreateDirectory(InstallDir);
            // Upgrade over a running copy: close its window (if open) and wait out a running --check.
            foreach (Process other in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledExe)))
            {
                try
                {
                    if (other.Id != Process.GetCurrentProcess().Id && other.SessionId == SessionId && other.MainWindowHandle != IntPtr.Zero)
                        other.CloseMainWindow();
                }
                catch (Exception ex) { Verbose("close old window: " + ex.Message); }
                finally { other.Dispose(); }
            }
            for (int attempt = 0; ; attempt++)
            {
                try { File.Copy(current, InstalledExe, true); break; }
                catch (IOException)
                {
                    if (attempt >= 30) throw;
                    Thread.Sleep(500);
                }
            }
            Log(L("Установлена копия: ", "Installed copy: ") + InstalledExe);
            RegisterTask();
            RegisterApp();
            MessageBox.Show(
                (upgrade ? L("Сторож обновлён до версии ", "The watchdog has been updated to version ") : L("Сторож установлен, версия ", "The watchdog is installed, version ")) + Version + ".\n\n"
                + L("Он уже работает в фоне. Найти его можно в меню «Пуск» («Сторож синхронизации»), удалить — в «Параметры → Приложения → Установленные приложения».\n\nФайл, который вы запускали, больше не нужен — его можно удалить.",
                    "It is already running in the background. Find it in the Start menu (“Sync Watchdog”); uninstall it in Settings → Apps → Installed apps.\n\nThe file you just ran is no longer needed and can be deleted."),
                L("Сторож синхронизации", "Sync Watchdog"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            ProcessStartInfo info = new ProcessStartInfo(InstalledExe);
            info.UseShellExecute = true;
            using (Process process = Process.Start(info)) { }
            return true;
        }

        private static string TaskXml()
        {
            StringBuilder xml = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings();
            settings.OmitXmlDeclaration = true;
            settings.Indent = true;
            using (XmlWriter writer = XmlWriter.Create(xml, settings))
            {
                const string ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                writer.WriteStartElement("Task", ns);
                writer.WriteAttributeString("version", "1.2");
                writer.WriteStartElement("Triggers", ns);
                writer.WriteStartElement("TimeTrigger", ns);
                writer.WriteStartElement("Repetition", ns);
                writer.WriteElementString("Interval", ns, "PT10M");
                writer.WriteElementString("StopAtDurationEnd", ns, "false");
                writer.WriteEndElement();
                writer.WriteElementString("StartBoundary", ns, DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                writer.WriteElementString("Enabled", ns, "true");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("Principals", ns);
                writer.WriteStartElement("Principal", ns);
                writer.WriteAttributeString("id", "Author");
                writer.WriteElementString("UserId", ns, Sid);
                writer.WriteElementString("LogonType", ns, "InteractiveToken");
                writer.WriteElementString("RunLevel", ns, "LeastPrivilege");
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteStartElement("Settings", ns);
                writer.WriteElementString("MultipleInstancesPolicy", ns, "IgnoreNew");
                writer.WriteElementString("DisallowStartIfOnBatteries", ns, "false");
                writer.WriteElementString("StopIfGoingOnBatteries", ns, "false");
                writer.WriteElementString("StartWhenAvailable", ns, "false");
                writer.WriteElementString("RunOnlyIfNetworkAvailable", ns, "false");
                writer.WriteElementString("Enabled", ns, "true");
                writer.WriteElementString("WakeToRun", ns, "false");
                writer.WriteElementString("ExecutionTimeLimit", ns, "PT2M");
                writer.WriteEndElement();
                writer.WriteStartElement("Actions", ns);
                writer.WriteAttributeString("Context", "Author");
                writer.WriteStartElement("Exec", ns);
                writer.WriteElementString("Command", ns, InstalledExe);
                writer.WriteElementString("Arguments", ns, "--check");
                writer.WriteElementString("WorkingDirectory", ns, InstallDir);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement();
            }
            return xml.ToString();
        }
        private static CommandResult TaskCommand(string arguments)
        {
            return RunCommand(SystemExe("schtasks.exe"), arguments, 15000);
        }
        internal const string Version = "1.6.0"; // keep in sync with AssemblyVersion at the top
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\SyncWatchdog";
        private static readonly string[] ShortcutNames = { "Сторож синхронизации.lnk", "Sync Watchdog.lnk" };
        private static string StartMenuDir() { return Environment.GetFolderPath(Environment.SpecialFolder.Programs); }

        // Start menu shortcut + entry in Settings → Apps → Installed apps (per user, no admin rights).
        private static void RegisterApp() { RegisterApp(Version); }
        private static void RegisterApp(string version)
        {
            try
            {
                string name = L("Сторож синхронизации", "Sync Watchdog");
                foreach (string old in ShortcutNames)
                {
                    string oldPath = Path.Combine(StartMenuDir(), old);
                    if (old != name + ".lnk" && File.Exists(oldPath)) File.Delete(oldPath);
                }
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                try
                {
                    object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell,
                        new object[] { Path.Combine(StartMenuDir(), name + ".lnk") });
                    Type linkType = link.GetType();
                    linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { InstalledExe });
                    linkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { InstallDir });
                    linkType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link,
                        new object[] { L("Следит за OneDrive и Dropbox", "Keeps OneDrive and Dropbox running") });
                    linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
                    Marshal.ReleaseComObject(link);
                }
                finally { Marshal.ReleaseComObject(shell); }
            }
            catch (Exception ex) { Log(L("Не удалось создать ярлык в «Пуске»: ", "Could not create the Start menu shortcut: ") + ex.Message); }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", L("Сторож синхронизации (Sync Watchdog)", "Sync Watchdog"));
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "Sync Watchdog");
                key.SetValue("DisplayIcon", InstalledExe);
                key.SetValue("InstallLocation", InstallDir);
                key.SetValue("UninstallString", Quote(InstalledExe) + " --uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
            }
        }

        // Full removal: task, shortcut, Installed apps entry, data, and the program folder (deleted by a short
        // delayed cmd after we exit, because this exe runs from that folder).
        private static void Uninstall()
        {
            TaskCommand("/Delete /F /TN " + Quote(TaskName)); // fine if the task is already gone
            foreach (string name in ShortcutNames)
            {
                try { File.Delete(Path.Combine(StartMenuDir(), name)); } catch (Exception) { }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception) { }
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); } catch (Exception) { }
            // Retry for up to ~60 s: the folder is locked until this process (and its message box) is gone.
            string dir = Quote(InstallDir);
            ProcessStartInfo info = new ProcessStartInfo(SystemExe("cmd.exe"),
                "/c for /l %i in (1,1,60) do @(ping 127.0.0.1 -n 2 >nul & rmdir /s /q " + dir + " 2>nul & if not exist " + dir + " exit)");
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WorkingDirectory = Path.GetTempPath();
            using (Process process = Process.Start(info)) { }
        }

        private static void RegisterTask()
        {
            string xml = Path.Combine(DataDir, "task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(xml, TaskXml(), Encoding.Unicode);
                CommandResult result = TaskCommand("/Create /F /TN " + Quote(TaskName) + " /XML " + Quote(xml));
                if (result.Code != 0) throw new InvalidOperationException(L("Не удалось создать задачу: ", "Could not create task: ") + result.Output + " (" + result.Code + ")");
                Log(L("Задача Планировщика установлена/обновлена", "Scheduled task installed/updated"));
            }
            finally { if (File.Exists(xml)) File.Delete(xml); }
        }

        private static void WriteDiagnostic(string file, string text)
        {
            WriteAtomic(DataPath(file), text + Environment.NewLine);
            // For a winexe the file is authoritative; console output is best effort when redirected.
            try { Console.WriteLine(text); } catch (IOException) { }
        }
        private static void WriteStatus()
        {
            CommandResult task = TaskCommand("/Query /TN " + Quote(TaskName) + " /XML");
            StringBuilder text = new StringBuilder();
            text.AppendLine(L("Время: ", "Time: ") + Stamp());
            text.AppendLine(L("Копия установлена: ", "Installed copy exists: ") + File.Exists(InstalledExe));
            text.AppendLine(L("Путь установки: ", "Install path: ") + InstalledExe);
            text.AppendLine(L("Запущенный файл: ", "Running executable: ") + Application.ExecutablePath);
            text.AppendLine(L("Задача найдена: ", "Task found: ") + (task.Code == 0) + " (exit=" + task.Code + ")");
            text.AppendLine(L("Каталог данных: ", "Data directory: ") + DataDir);
            Settings settings = Load<Settings>("settings.json");
            foreach (string app in Apps) text.AppendLine(app + ": " + (FindClient(app) ?? L("не установлен", "not installed")) + "; watch=" + settings.Watches(app) + "; running=" + IsRunning(app));
            text.AppendLine(task.Output);
            if (File.Exists(DataPath("last-check.txt"))) text.AppendLine(File.ReadAllText(DataPath("last-check.txt"), Utf8));
            WriteDiagnostic("status.txt", text.ToString());
        }
        private static void DumpTray()
        {
            List<TrayEntry> entries = ReadTray();
            StringBuilder text = new StringBuilder(Stamp() + Environment.NewLine);
            text.AppendLine(L("Панель переполнения не открывалась. Отсутствующие UIA-элементы сюда не попадают.", "Overflow was not opened. Elements not exposed by UIA are absent from this dump."));
            foreach (TrayEntry entry in entries) text.AppendLine(entry.Surface + "\t" + entry.Type + "\t" + entry.ClassName + "\t" + entry.Where + "\t" + entry.Name);
            text.AppendLine(L("Итого именованных элементов: ", "Named elements: ") + entries.Count);
            WriteDiagnostic("tray-dump.txt", text.ToString());
        }

        private static string LastCheckText()
        {
            if (!File.Exists(DataPath("last-check.txt"))) return L("Первая проверка пройдёт в течение минуты.", "The first check will run within a minute.");
            List<string> lines = new List<string>();
            foreach (string line in File.ReadAllLines(DataPath("last-check.txt"), Utf8))
            {
                if (line.Length == 0) continue;
                // Update lines are stale once this version is installed (e.g. right after "Update to …").
                Match update = Regex.Match(line, @"^(?:Обновлён до версии|Updated to version|Доступна новая версия|New version available:) ([\d.]+)");
                if (update.Success)
                {
                    try { if (new Version(update.Groups[1].Value) <= CurrentVersion()) continue; }
                    catch (Exception) { }
                }
                string clean = Regex.Replace(line, ": «.*» — ", " — ");
                lines.Add(line.StartsWith("Last check:", StringComparison.Ordinal) || line.StartsWith("Последняя проверка:", StringComparison.Ordinal) ? clean : "• " + clean);
            }
            return String.Join(Environment.NewLine, lines.ToArray());
        }
        private static void InstallAndShow()
        {
            RegisterTask();
            RegisterApp();
            Settings settings = Load<Settings>("settings.json");
            bool oneInstalled = FindClient("OneDrive") != null;
            bool dropInstalled = FindClient("Dropbox") != null;
            using (Form form = new Form())
            {
                form.Text = L("Сторож синхронизации", "Sync Watchdog");
                form.TopMost = true;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.AutoScaleMode = AutoScaleMode.Dpi;
                // Window sizes itself to the content: no empty stretch area.
                form.AutoSize = true;
                form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.MaximizeBox = false;
                form.MinimizeBox = false;
                form.Font = new Font("Segoe UI", 10);
                TableLayoutPanel layout = new TableLayoutPanel();
                layout.AutoSize = true;
                layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                layout.Padding = new Padding(18);
                layout.ColumnCount = 1;
                layout.RowCount = 9;
                for (int i = 0; i < 9; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                form.Controls.Add(layout);
                Label intro = new Label();
                intro.AutoSize = true;
                intro.MaximumSize = new Size(610, 0);
                intro.Text = L("Сторож включён и работает в фоне, окон не показывает. Раз в 10 минут он проверяет OneDrive и Dropbox и перезапускает их, если они закрылись или зависли.", "The watchdog is on and works in the background without windows. Every 10 minutes it checks OneDrive and Dropbox and restarts them if they quit or hang.");
                layout.Controls.Add(intro, 0, 0);
                Label last = new Label();
                last.AutoSize = true;
                last.MaximumSize = new Size(610, 0);
                last.Text = LastCheckText();
                last.Margin = new Padding(3, 14, 3, 12);
                layout.Controls.Add(last, 0, 1);
                CheckBox one = new CheckBox();
                one.AutoSize = true;
                one.Enabled = oneInstalled;
                one.Checked = oneInstalled && settings.OneDrive;
                one.Text = L("Следить за OneDrive", "Monitor OneDrive") + (oneInstalled ? "" : L(" (не установлен)", " (not installed)"));
                layout.Controls.Add(one, 0, 2);
                CheckBox drop = new CheckBox();
                drop.AutoSize = true;
                drop.Enabled = dropInstalled;
                drop.Checked = dropInstalled && settings.Dropbox;
                drop.Text = L("Следить за Dropbox", "Monitor Dropbox") + (dropInstalled ? "" : L(" (не установлен)", " (not installed)"));
                layout.Controls.Add(drop, 0, 3);
                Label hint = new Label();
                hint.AutoSize = true;
                hint.MaximumSize = new Size(610, 0);
                hint.Margin = new Padding(3, 12, 3, 0);
                hint.ForeColor = SystemColors.GrayText;
                CheckBox promote = new CheckBox();
                promote.AutoSize = true;
                promote.Checked = settings.AlwaysShowIcons;
                promote.Text = L("Всегда показывать значки OneDrive и Dropbox на панели задач", "Always show the OneDrive and Dropbox icons on the taskbar");
                layout.Controls.Add(promote, 0, 4);
                hint.Margin = new Padding(22, 0, 3, 0);
                hint.Text = L("Иначе Windows прячет их под стрелку «^», и сторож не видит зависаний.", "Otherwise Windows hides them under the “^” arrow and the watchdog cannot see hangs.");
                layout.Controls.Add(hint, 0, 5);
                CheckBox autoUpdate = new CheckBox();
                autoUpdate.AutoSize = true;
                autoUpdate.Margin = new Padding(3, 10, 3, 3);
                autoUpdate.Checked = settings.AutoUpdate;
                autoUpdate.Text = L("Обновлять автоматически", "Update automatically");
                layout.Controls.Add(autoUpdate, 0, 6);
                UpdateState updateState = Load<UpdateState>("update.json");
                Version pending = null;
                try { if (updateState.latest.Length > 0 && new Version(updateState.latest) > CurrentVersion()) pending = new Version(updateState.latest); }
                catch (Exception) { }
                FlowLayoutPanel versionRow = new FlowLayoutPanel();
                versionRow.AutoSize = true;
                versionRow.Margin = new Padding(0, 4, 0, 0);
                Label versionLabel = new Label();
                versionLabel.AutoSize = true;
                versionLabel.Margin = new Padding(3, 7, 10, 0);
                versionLabel.ForeColor = SystemColors.GrayText;
                versionLabel.Text = L("Версия ", "Version ") + Version + (pending != null ? L(" · доступна ", " · available: ") + pending : "");
                Button updateButton = new Button();
                updateButton.AutoSize = true;
                updateButton.Text = pending != null ? L("Обновить до ", "Update to ") + pending : L("Проверить обновления", "Check for updates");
                versionRow.Controls.Add(versionLabel);
                versionRow.Controls.Add(updateButton);
                layout.Controls.Add(versionRow, 0, 7);
                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.AutoSize = true;
                buttons.Dock = DockStyle.Fill;
                buttons.Margin = new Padding(0, 18, 0, 0);
                layout.Controls.Add(buttons, 0, 8);
                form.Icon = AppIcon();
                updateButton.Click += delegate
                {
                    try
                    {
                        Cursor.Current = Cursors.WaitCursor;
                        ReleaseInfo release = LatestRelease();
                        Cursor.Current = Cursors.Default;
                        if (release == null || release.Version <= CurrentVersion())
                        {
                            versionLabel.Text = L("Версия ", "Version ") + Version;
                            updateButton.Text = L("Проверить обновления", "Check for updates");
                            MessageBox.Show(form, L("У вас последняя версия.", "You have the latest version."), form.Text);
                            return;
                        }
                        if (MessageBox.Show(form, L("Доступна версия ", "Version ") + release.Version + L(". Обновить сейчас?", " is available. Update now?"),
                            form.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        Cursor.Current = Cursors.WaitCursor;
                        InstallUpdate(release);
                        Cursor.Current = Cursors.Default;
                        UpdateState done = Load<UpdateState>("update.json");
                        done.latest = "";
                        Save("update.json", done);
                        MessageBox.Show(form, L("Сторож обновлён до версии ", "The watchdog has been updated to version ") + release.Version + ".", form.Text);
                        using (Process process = Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true })) { }
                        form.Close();
                    }
                    catch (Exception ex)
                    {
                        Cursor.Current = Cursors.Default;
                        MessageBox.Show(form, L("Не удалось обновиться: ", "Update failed: ") + ex.Message, form.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                };
                Button ok = new Button();
                ok.Text = "OK";
                ok.AutoSize = true;
                ok.Click += delegate { form.Close(); };
                buttons.Controls.Add(ok);
                form.AcceptButton = ok;
                form.ActiveControl = ok;
                Button open = new Button();
                open.Text = L("Открыть журнал", "Open log");
                open.AutoSize = true;
                open.Click += delegate
                {
                    try
                    {
                        if (!File.Exists(DataPath("journal.log"))) File.WriteAllText(DataPath("journal.log"), L("Журнал пуст: сторож пока ничего не делал.\r\n", "The log is empty: the watchdog has not done anything yet.\r\n"), Utf8);
                        using (Process process = Process.Start(SystemExe("notepad.exe"), Quote(DataPath("journal.log")))) { }
                    }
                    catch (Exception ex) { MessageBox.Show(form, ex.Message, form.Text); }
                };
                buttons.Controls.Add(open);
                Button uninstall = new Button();
                uninstall.Text = L("Удалить сторожа", "Uninstall watchdog");
                uninstall.AutoSize = true;
                bool removed = false;
                uninstall.Click += delegate
                {
                    try
                    {
                        if (MessageBox.Show(form, L("Удалить сторожа полностью: задачу, ярлык, программу, журнал и настройки?", "Remove the watchdog completely: task, shortcut, program, log and settings?"),
                            form.Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        Uninstall();
                        removed = true;
                        MessageBox.Show(form, L("Сторож удалён.", "The watchdog has been removed."), form.Text);
                        form.Close();
                    }
                    catch (Exception ex) { MessageBox.Show(form, ex.Message, form.Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                buttons.Controls.Add(uninstall);
                Action persist = delegate
                {
                    // Keep defaults for absent clients so a future installation is monitored automatically.
                    if (oneInstalled) settings.OneDrive = one.Checked;
                    if (dropInstalled) settings.Dropbox = drop.Checked;
                    settings.AlwaysShowIcons = promote.Checked;
                    settings.AutoUpdate = autoUpdate.Checked;
                    Save("settings.json", settings);
                    PromoteIcons(settings);
                };
                EventHandler changed = delegate
                {
                    try { persist(); }
                    catch (Exception ex) { MessageBox.Show(form, ex.Message, form.Text, MessageBoxButtons.OK, MessageBoxIcon.Error); }
                };
                one.CheckedChanged += changed;
                drop.CheckedChanged += changed;
                promote.CheckedChanged += changed;
                autoUpdate.CheckedChanged += changed;
                form.FormClosing += delegate(object sender, FormClosingEventArgs e)
                {
                    if (removed) return;
                    try { persist(); }
                    catch (Exception ex) { e.Cancel = true; MessageBox.Show(form, ex.Message, form.Text); }
                };
                persist();
                Application.Run(form);
            }
        }
    }
}
