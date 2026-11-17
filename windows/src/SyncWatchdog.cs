// Sync Watchdog for Windows 10/11. C# 5 / .NET Framework 4.x.
// No Explorer interaction: UI Automation reads existing tray surfaces only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

namespace SyncWatchdog
{
    public sealed class ClientState
    {
        public string prev = "";
        public int cnt = 0;
        public bool doneRestart = false;
        public bool warned = false;
    }

    public sealed class Settings
    {
        public bool OneDrive = true;
        public bool Dropbox = true;
        public bool AlwaysShowIcons = true;
        public bool Watches(string name) { return name == "OneDrive" ? OneDrive : Dropbox; }
    }

    internal sealed class TrayEntry
    {
        public string Surface;
        public string Name;
        public string ClassName;
        public string Type;
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
                    using (Mutex mutex = new Mutex(false, "Local\\SyncWatchdog-Check-" + Sid))
                    {
                        if (!Acquire(mutex)) return 0;
                        try { RunCheck(); ShowNotifications(); }
                        finally { mutex.ReleaseMutex(); }
                    }
                }
                else if (args.Length == 1 && args[0] == "--status") WriteStatus();
                else if (args.Length == 1 && args[0] == "--dump-tray") DumpTray();
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
                ProcessStartInfo info = new ProcessStartInfo(path, app == "OneDrive" ? "/background" : "/home");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
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
                List<Process> processes = ClientProcesses(app);
                try
                {
                    foreach (Process process in processes)
                    {
                        try
                        {
                            if (!process.CloseMainWindow())
                                RunCommand(SystemExe("taskkill.exe"), "/PID " + process.Id.ToString(CultureInfo.InvariantCulture), 3000);
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception ex) { Verbose(ex.Message); }
                    }
                }
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
        private enum Busy { No, Yes, Skip }
        private static Busy Classify(string text)
        {
            string s = text.ToLowerInvariant();
            if (ContainsAny(s, new string[] { "выполнено", "обновлено", "up to date", "files are up to date", "синхронизировано" })) return Busy.No;
            if (ContainsAny(s, new string[] { "приостановл", "пауз", "нет подключения", "не в сети", "ошибк", "требуется", "войдите", "paused", "offline", "not connected", "error", "sign in", "signed out" })) return Busy.Skip;
            if (ContainsAny(s, new string[] { "отправк", "скачиван", "загрузк", "соединени", "подключени", "синхрониз", "обработк", "индекс", "подготовк", "проверк", "upload", "download", "connecting", "syncing", "processing", "indexing", "preparing" })) return Busy.Yes;
            return Busy.No;
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
                                entries.Add(new TrayEntry { Surface = surface, Name = name, ClassName = info.ClassName, Type = info.ControlType.ProgrammaticName });
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
                                    Log(L("Показ значка в трее включён: ", "Enabled tray icon visibility: ") + exe);
                                }
                            }
                        }
                        catch (Exception ex) { Verbose("NotifyIconSettings: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { Verbose("NotifyIconSettings: " + ex.Message); }
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
                        if (String.IsNullOrEmpty(status))
                        {
                            if (state.prev != "__nostatus__") Log(app + L(": значок не найден в трее", ": tray icon not found"));
                            // A missing observation is not a changed client text. Keep episode safety flags.
                            state.prev = "__nostatus__";
                            state.cnt = 0;
                            Save(app + ".json", state);
                            report.Add(app + L(": значок не найден в трее", ": tray icon not found"));
                            continue;
                        }
                        Busy busy = Classify(status);
                        if (busy == Busy.Yes && status == state.prev) state.cnt = Math.Min(state.cnt + 1, 100000);
                        else
                        {
                            state.cnt = 0;
                            if (status != state.prev) { state.doneRestart = false; state.warned = false; }
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
                                else if (!state.doneRestart)
                                {
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
                            : (busy == Busy.Skip ? L("пауза/нет сети — не трогаю", "paused/offline — leaving alone") : L("в порядке", "OK"));
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
                // Unlike the Mac version, test runs also update last-check, as required by the Windows spec.
                string test = Environment.GetEnvironmentVariable("FAKE_OneDrive") != null || Environment.GetEnvironmentVariable("FAKE_Dropbox") != null
                    ? L(" [ТЕСТ FAKE]", " [FAKE TEST]") : "";
                WriteAtomic(DataPath("last-check.txt"), L("Последняя проверка: ", "Last check: ") + Stamp() + test + Environment.NewLine + String.Join(Environment.NewLine, report.ToArray()) + Environment.NewLine);
            }
        }

        private static void ShowNotifications()
        {
            if (Notifications.Count == 0) return;
            using (NotifyIcon icon = new NotifyIcon())
            using (System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer())
            {
                icon.Icon = SystemIcons.Warning;
                icon.Text = "Sync Watchdog";
                icon.Visible = true;
                List<string> titles = new List<string>();
                List<string> bodies = new List<string>();
                foreach (string[] item in Notifications) { titles.Add(item[0]); bodies.Add(item[1]); }
                string title = String.Join("; ", titles.ToArray());
                string body = String.Join(Environment.NewLine, bodies.ToArray());
                if (title.Length > 63) title = title.Substring(0, 60) + "...";
                if (body.Length > 250) body = body.Substring(0, 247) + "...";
                timer.Interval = 11000;
                timer.Tick += delegate { timer.Stop(); Application.ExitThread(); };
                // Keep a message loop alive: disposing immediately would discard the balloon.
                icon.ShowBalloonTip(10000, title, body, ToolTipIcon.Warning);
                timer.Start();
                Application.Run();
                icon.Visible = false;
            }
        }

        private static bool RelaunchInstalledCopy()
        {
            string current = Path.GetFullPath(Application.ExecutablePath);
            if (String.Equals(current, Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase)) return false;
            Directory.CreateDirectory(InstallDir);
            File.Copy(current, InstalledExe, true);
            Log(L("Установлена копия: ", "Installed copy: ") + InstalledExe);
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
            text.AppendLine("AlwaysShowIcons=" + settings.AlwaysShowIcons);
            text.AppendLine(task.Output);
            if (File.Exists(DataPath("last-check.txt"))) text.AppendLine(File.ReadAllText(DataPath("last-check.txt"), Utf8));
            WriteDiagnostic("status.txt", text.ToString());
        }
        private static void DumpTray()
        {
            List<TrayEntry> entries = ReadTray();
            StringBuilder text = new StringBuilder(Stamp() + Environment.NewLine);
            text.AppendLine(L("Панель переполнения не открывалась. Отсутствующие UIA-элементы сюда не попадают.", "Overflow was not opened. Elements not exposed by UIA are absent from this dump."));
            foreach (TrayEntry entry in entries) text.AppendLine(entry.Surface + "\t" + entry.Type + "\t" + entry.ClassName + "\t" + entry.Name);
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
                string clean = Regex.Replace(line, ": «.*» — ", " — ");
                lines.Add(line.StartsWith("Last check:", StringComparison.Ordinal) || line.StartsWith("Последняя проверка:", StringComparison.Ordinal) ? clean : "• " + clean);
            }
            return String.Join(Environment.NewLine, lines.ToArray());
        }
        private static void InstallAndShow()
        {
            RegisterTask();
            Settings settings = Load<Settings>("settings.json");
            bool oneInstalled = FindClient("OneDrive") != null;
            bool dropInstalled = FindClient("Dropbox") != null;
            using (Form form = new Form())
            {
                form.Text = L("Сторож синхронизации", "Sync Watchdog");
                form.TopMost = true;
                form.StartPosition = FormStartPosition.CenterScreen;
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.ClientSize = new Size(660, 480);
                form.MinimumSize = new Size(620, 480);
                form.Font = new Font("Segoe UI", 10);
                TableLayoutPanel layout = new TableLayoutPanel();
                layout.Dock = DockStyle.Fill;
                layout.Padding = new Padding(18);
                layout.ColumnCount = 1;
                layout.RowCount = 7;
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                for (int i = 2; i < 7; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                form.Controls.Add(layout);
                Label intro = new Label();
                intro.AutoSize = true;
                intro.MaximumSize = new Size(610, 0);
                intro.Text = L("Сторож включён и работает в фоне, окон не показывает. Раз в 10 минут он проверяет OneDrive и Dropbox и перезапускает их, если они закрылись или зависли.", "The watchdog is on and works in the background without windows. Every 10 minutes it checks OneDrive and Dropbox and restarts them if they quit or hang.");
                layout.Controls.Add(intro, 0, 0);
                TextBox last = new TextBox();
                last.Multiline = true;
                last.ReadOnly = true;
                last.BorderStyle = BorderStyle.None;
                last.BackColor = form.BackColor;
                last.ScrollBars = ScrollBars.Vertical;
                last.Dock = DockStyle.Fill;
                last.Text = LastCheckText();
                last.Margin = new Padding(0, 18, 0, 12);
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
                CheckBox promote = new CheckBox();
                promote.AutoSize = true;
                promote.Checked = settings.AlwaysShowIcons;
                promote.Text = L("Всегда показывать значки OneDrive и Dropbox в трее", "Always show OneDrive and Dropbox icons in the tray");
                layout.Controls.Add(promote, 0, 4);
                Label hint = new Label();
                hint.AutoSize = true;
                hint.MaximumSize = new Size(600, 0);
                hint.ForeColor = SystemColors.GrayText;
                hint.Text = L("Иначе Windows прячет их под стрелку и сторож не видит зависаний. Снятие галочки не скрывает значки обратно.", "Otherwise Windows hides them under the arrow and the watchdog cannot detect hangs. Unchecking does not hide icons again.");
                layout.Controls.Add(hint, 0, 5);
                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.AutoSize = true;
                buttons.Dock = DockStyle.Fill;
                buttons.Margin = new Padding(0, 18, 0, 0);
                layout.Controls.Add(buttons, 0, 6);
                Button ok = new Button();
                ok.Text = "OK";
                ok.AutoSize = true;
                ok.Click += delegate { form.Close(); };
                buttons.Controls.Add(ok);
                form.AcceptButton = ok;
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
                        CommandResult result = TaskCommand("/Delete /F /TN " + Quote(TaskName));
                        if (result.Code != 0) throw new InvalidOperationException(result.Output);
                        removed = true;
                        Log(L("Задача удалена, сторож выключен", "Task deleted, watchdog disabled"));
                        MessageBox.Show(form, L("Сторож выключен. Уже запущенная проверка может ещё завершаться. Теперь удалите папку:\n", "The watchdog is disabled. An active check may still be finishing. Now delete the folder:\n") + InstallDir, form.Text);
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
