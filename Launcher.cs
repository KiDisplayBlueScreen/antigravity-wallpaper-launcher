using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal sealed class Settings
{
    public string WallpaperPath { get; set; }
    public string ApplicationPath { get; set; }
    public int Brightness { get; set; }
    public int SidebarTransparency { get; set; }
    public int TextMaskStrength { get; set; }
    public bool Enabled { get; set; }
    public Settings()
    {
        WallpaperPath = Path.Combine(Program.DirectoryPath, "wallpaper.jpg");
        ApplicationPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe");
        Brightness = 76;
        SidebarTransparency = 58;
        TextMaskStrength = 65;
        Enabled = true;
    }
    public void Validate(bool checkImage)
    {
        if (Brightness < 0 || Brightness > 150 || SidebarTransparency < 0 || SidebarTransparency > 100 || TextMaskStrength < 0 || TextMaskStrength > 100)
            throw new Exception("Brightness must be 0–150%; sidebar transparency and text mask strength must be 0–100%.");
        if (checkImage)
        {
            if (!File.Exists(WallpaperPath)) throw new Exception("Wallpaper file not found. Select an image file.");
            string ext = Path.GetExtension(WallpaperPath).ToLowerInvariant();
            if (!new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp" }.Contains(ext))
                throw new Exception("Select a JPG, PNG, WebP, GIF or BMP wallpaper.");
        }
    }
}

internal static class Program
{
    internal static readonly string DirectoryPath = AppDomain.CurrentDomain.BaseDirectory;
    internal static readonly string ConfigPath = Path.Combine(DirectoryPath, "settings.json");
    internal static EventWaitHandle ShowSignal, StartSignal;
    internal static string Json(object value) { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(value); }
    internal static Settings Load()
    {
        if (!File.Exists(ConfigPath)) return new Settings();
        var settings = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(ConfigPath, Encoding.UTF8));
        if (settings == null) throw new Exception("settings.json is empty or invalid. Restore a valid configuration.");
        if (!string.IsNullOrWhiteSpace(settings.WallpaperPath) && !Path.IsPathRooted(settings.WallpaperPath))
            settings.WallpaperPath = Path.GetFullPath(Path.Combine(DirectoryPath, settings.WallpaperPath));
        settings.Validate(false);
        return settings;
    }
    internal static void Save(Settings settings)
    {
        settings.Validate(settings.Enabled);
        string pending = ConfigPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string image = settings.WallpaperPath;
        if (!string.IsNullOrWhiteSpace(image) && Path.GetDirectoryName(Path.GetFullPath(image)).TrimEnd(Path.DirectorySeparatorChar).Equals(DirectoryPath.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            image = Path.GetFileName(image);
        var stored = new Settings { WallpaperPath = image, ApplicationPath = settings.ApplicationPath, Brightness = settings.Brightness, SidebarTransparency = settings.SidebarTransparency, TextMaskStrength = settings.TextMaskStrength, Enabled = settings.Enabled };
        File.WriteAllText(pending, Json(stored), new UTF8Encoding(false));
        try
        {
            if (File.Exists(ConfigPath)) File.Replace(pending, ConfigPath, null);
            else File.Move(pending, ConfigPath);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
    [STAThread]
    private static void Main(string[] args)
    {
        // This diagnostic route only attaches to an already running GUI; it never starts an app.
        if (args.Contains("--apply-once"))
        {
            string report = Path.Combine(DirectoryPath, "apply-result.json");
            try
            {
                string result = Injector.Apply(Load(), CancellationToken.None).GetAwaiter().GetResult();
                File.WriteAllText(report, result, new UTF8Encoding(false));
                Environment.ExitCode = result.Contains("\"applied\":true") || result.Contains("\"removed\":true") ? 0 : 1;
            }
            catch (Exception error) { File.WriteAllText(report, Json(new { error = error.Message })); Environment.ExitCode = 1; }
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        bool first;
        string instance = "Local\\AntigravityWallpaperLauncher-" + StableName(DirectoryPath);
        using (var mutex = new Mutex(true, instance, out first))
        using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-show"))
        using (var start = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-start"))
        {
            ShowSignal = show; StartSignal = start;
            if (!first) { (args.Contains("--start") ? start : show).Set(); return; }
            Application.Run(new LauncherForm(args.Contains("--start")));
        }
    }
    private static string StableName(string value)
    {
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value.ToLowerInvariant()))).Replace("-", "").Substring(0, 20);
    }
}

internal static class Injector
{
    private static readonly string DevToolsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Antigravity", "DevToolsActivePort");
    private static string cachedImagePath, cachedDataUrl;
    private static DateTime cachedImageTime;
    private static long cachedImageLength;
    private static async Task<string> Http(string url, CancellationToken cancellation)
    {
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Proxy = null;
        request.Timeout = 3000;
        using (cancellation.Register(request.Abort))
        using (var response = await request.GetResponseAsync().ConfigureAwait(false))
        using (var reader = new StreamReader(response.GetResponseStream()))
            return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
    private static Uri LocalSocket(string value)
    {
        Uri uri;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "ws" || !uri.IsLoopback)
            throw new Exception("Antigravity did not return a local debugging connection.");
        return uri;
    }
    private static async Task<Dictionary<string, object>> Evaluate(Uri socket, string expression, CancellationToken cancellation)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        using (var ws = new ClientWebSocket())
        {
            timeout.CancelAfter(12000);
            ws.Options.Proxy = null;
            await ws.ConnectAsync(socket, timeout.Token).ConfigureAwait(false);
            var payload = Encoding.UTF8.GetBytes(Program.Json(new {
                id = 1, method = "Runtime.evaluate", @params = new { expression = expression, returnByValue = true, awaitPromise = true }
            }));
            await ws.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
            byte[] buffer = new byte[8192];
            for (;;)
            {
                using (var message = new MemoryStream())
                {
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);
                        if (received.MessageType == WebSocketMessageType.Close) throw new Exception("Antigravity closed the preview connection. It will reconnect automatically.");
                        message.Write(buffer, 0, received.Count);
                        if (message.Length > 2 * 1024 * 1024) throw new Exception("Unexpected debugging response size.");
                    } while (!received.EndOfMessage);
                    var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(message.ToArray()));
                    if (!json.ContainsKey("id")) continue;
                    if (json.ContainsKey("error")) throw new Exception("Antigravity rejected the wallpaper script.");
                    var result = (Dictionary<string, object>)json["result"];
                    if (result.ContainsKey("exceptionDetails")) throw new Exception("The wallpaper could not be applied. Check the image file and Antigravity version.");
                    var remote = (Dictionary<string, object>)result["result"];
                    if (!remote.ContainsKey("value")) throw new Exception("Antigravity did not confirm the wallpaper result.");
                    return (Dictionary<string, object>)remote["value"];
                }
            }
        }
    }
    private static string DataUrl(Settings settings)
    {
        var file = new FileInfo(settings.WallpaperPath);
        if (cachedImagePath == file.FullName && cachedImageTime == file.LastWriteTimeUtc && cachedImageLength == file.Length && cachedDataUrl != null)
            return cachedDataUrl;
        string ext = file.Extension.ToLowerInvariant();
        string mime = ext == ".png" ? "image/png" : ext == ".webp" ? "image/webp" : ext == ".gif" ? "image/gif" : ext == ".bmp" ? "image/bmp" : "image/jpeg";
        cachedDataUrl = "data:" + mime + ";base64," + Convert.ToBase64String(File.ReadAllBytes(file.FullName));
        cachedImagePath = file.FullName;
        cachedImageTime = file.LastWriteTimeUtc;
        cachedImageLength = file.Length;
        return cachedDataUrl;
    }
    internal static async Task<string> Apply(Settings settings, CancellationToken cancellation)
    {
        settings.Validate(settings.Enabled);
        if (!File.Exists(DevToolsPath)) throw new Exception("Waiting for Antigravity. Click Start Antigravity to open it.");
        int port;
        if (!int.TryParse(File.ReadLines(DevToolsPath).FirstOrDefault(), out port) || port < 1 || port > 65535)
            throw new Exception("Waiting for Antigravity to create its preview connection.");
        string targetJson = await Http("http://127.0.0.1:" + port + "/json/list", cancellation).ConfigureAwait(false);
        var targets = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(targetJson);
        var target = targets.FirstOrDefault(item => {
            Uri url;
            return item.ContainsKey("type") && (string)item["type"] == "page" && item.ContainsKey("title") && ((string)item["title"] == "Antigravity" || ((string)item["title"]).EndsWith(" - Antigravity", StringComparison.Ordinal))
                && item.ContainsKey("url") && Uri.TryCreate((string)item["url"], UriKind.Absolute, out url) && url.IsLoopback && (url.Scheme == "https" || url.Scheme == "http");
        });
        if (target == null) throw new Exception("Waiting for the Antigravity interface to finish loading.");
        Uri socket = LocalSocket((string)target["webSocketDebuggerUrl"]);
        if (!settings.Enabled)
            return Program.Json(await Evaluate(socket, "(()=>{document.getElementById('plana-wallpaper-preview')?.remove();delete window.__planaWallpaperState;return {removed:true};})()", cancellation).ConfigureAwait(false));
        var file = new FileInfo(settings.WallpaperPath);
        string key = Program.Json(new { path = file.FullName, time = file.LastWriteTimeUtc.Ticks, length = file.Length, brightness = settings.Brightness, transparency = settings.SidebarTransparency, textMask = settings.TextMaskStrength, version = 3 });
        var state = await Evaluate(socket, "(()=>({current:window.__planaWallpaperState===" + Program.Json(key) + "&&!!document.getElementById('plana-wallpaper-preview'),shell:!!document.querySelector('.agy-shell')}))()", cancellation).ConfigureAwait(false);
        if (state.ContainsKey("current") && (bool)state["current"]) return Program.Json(new { applied = true, unchanged = true, brightness = settings.Brightness, sidebarTransparency = settings.SidebarTransparency, textMaskStrength = settings.TextMaskStrength });
        string expression = BuildScript(settings, key, DataUrl(settings));
        return Program.Json(await Evaluate(socket, expression, cancellation).ConfigureAwait(false));
    }
    private static string BuildScript(Settings settings, string key, string image)
    {
        var values = new { key = key, image = image, brightness = settings.Brightness, transparency = settings.SidebarTransparency, textMask = settings.TextMaskStrength };
        return @"(async v=>{
const shell=document.querySelector('.agy-shell');
if(!shell) return {applied:false,reason:'Waiting for Antigravity layout'};
const image=new Image();image.src=v.image;await image.decode();
const mainSelector='.browser-hole-opaque > .flex-1.min-h-0.flex.flex-col';
document.querySelectorAll(mainSelector).forEach(e=>e.setAttribute('data-plana-wallpaper-content',''));
document.getElementById('plana-wallpaper-preview')?.remove();
const style=document.createElement('style');style.id='plana-wallpaper-preview';
const fadeSelector="".agy-shell .absolute.bottom-0.pointer-events-none[style*='linear-gradient(to top, var(--background), transparent)']"";
style.textContent=`
.agy-shell{position:relative!important;isolation:isolate;background:transparent!important;}
.agy-shell::before{content:'';position:absolute;inset:0;z-index:-1;pointer-events:none;background-image:url('${v.image}');background-size:cover;background-position:center;background-repeat:no-repeat;filter:brightness(${v.brightness/100});}
.agy-shell .agy-inset-shell{background:transparent!important;}
.agy-shell .bg-sidebar{background-color:color-mix(in srgb,var(--sidebar,var(--background)) ${100-v.transparency}%,transparent)!important;}
.agy-shell [data-plana-wallpaper-content],.agy-shell ${mainSelector}{background:color-mix(in srgb,var(--background) 10%,transparent)!important;}
.agy-shell [data-testid='agent-response'],.agy-shell [data-testid='user-message-bubble']{background-color:color-mix(in srgb,var(--background) ${v.textMask}%,transparent)!important;}
.agy-shell [data-testid='agent-response']{border-radius:var(--radius-lg,12px);}
.agy-shell [role='navigation'] .text-muted-foreground{color:color-mix(in srgb,var(--foreground) 90%,transparent)!important;}
${fadeSelector}{background-image:none!important;}
`;
document.head.append(style);window.__planaWallpaperState=v.key;
await new Promise(requestAnimationFrame);
return {applied:true,brightness:v.brightness,sidebarTransparency:v.transparency,textMaskStrength:v.textMask,imageWidth:image.naturalWidth,imageHeight:image.naturalHeight,navigationBackground:getComputedStyle(document.querySelector('[role=navigation]')||shell).backgroundColor,hiddenBottomFades:document.querySelectorAll(fadeSelector).length};
})(" + Program.Json(values) + ")";
    }
}

internal sealed class LauncherForm : Form
{
    private readonly TextBox wallpaper = new TextBox(), applicationPath = new TextBox();
    private readonly TrackBar brightness = new TrackBar(), transparency = new TrackBar(), textMask = new TrackBar();
    private readonly Label brightnessValue = new Label(), transparencyValue = new Label(), textMaskValue = new Label(), status = new Label();
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
    private readonly System.Windows.Forms.Timer signals = new System.Windows.Forms.Timer();
    private Settings settings;
    private bool exiting, changing;
    private string startupError;
    internal LauncherForm(bool start)
    {
        SuspendLayout();
        Text = "Antigravity Wallpaper Launcher";
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 10F);
        ClientSize = new Size(700, 480);
        MinimumSize = new Size(600, 500);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        try { settings = Program.Load(); }
        catch (Exception error) { settings = new Settings(); startupError = error.Message; }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), ColumnCount = 3, RowCount = 13, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Controls.Add(layout);
        AddLabel(layout, "Wallpaper settings", 0, true);
        AddLabel(layout, "Wallpaper file", 1, false);
        wallpaper.Text = settings.WallpaperPath;
        AddFileRow(layout, wallpaper, 2, "Choose wallpaper", "Images|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.bmp", false);
        AddLabel(layout, "Wallpaper brightness (100% = original)", 3, false);
        AddSlider(layout, brightness, brightnessValue, 4, 0, 150, settings.Brightness, "Wallpaper brightness");
        AddLabel(layout, "Sidebar transparency (100% = transparent)", 5, false);
        AddSlider(layout, transparency, transparencyValue, 6, 0, 100, settings.SidebarTransparency, "Sidebar transparency");
        AddLabel(layout, "Text mask strength (100% = opaque)", 7, false);
        AddSlider(layout, textMask, textMaskValue, 8, 0, 100, settings.TextMaskStrength, "Text mask strength");
        AddLabel(layout, "Antigravity application", 9, false);
        applicationPath.Text = settings.ApplicationPath;
        AddFileRow(layout, applicationPath, 10, "Choose Antigravity", "Antigravity|Antigravity.exe|Executables|*.exe", true);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Padding = new Padding(0, 12, 0, 4) };
        layout.Controls.Add(buttons, 0, 11); layout.SetColumnSpan(buttons, 3);
        AddButton(buttons, "Save && Apply", delegate { SaveAndApply(false); });
        AddButton(buttons, "Start Antigravity", delegate { SaveAndApply(true); });
        AddButton(buttons, "Restore default", delegate { DisableWallpaper(); });
        AddButton(buttons, "Minimize to tray", delegate { MinimizeToTray(); });
        AddButton(buttons, "Exit", delegate { Quit(); });
        status.Text = "Close: run in the background without a tray icon. Minimize to tray: keep a tray icon.";
        status.AutoSize = true; status.MaximumSize = new Size(640, 0); status.Dock = DockStyle.Fill; status.Padding = new Padding(0, 10, 0, 0);
        layout.Controls.Add(status, 0, 12); layout.SetColumnSpan(status, 3);
        wallpaper.AccessibleName = "Wallpaper file"; applicationPath.AccessibleName = "Antigravity application";
        tray.Icon = Icon;
        tray.Text = "Antigravity Wallpaper Launcher";
        tray.Visible = false;
        tray.DoubleClick += delegate { ShowPanel(); };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open settings", null, delegate { ShowPanel(); });
        menu.Items.Add("Start Antigravity", null, delegate { SaveAndApply(true); });
        menu.Items.Add("Restore default", null, delegate { DisableWallpaper(); });
        menu.Items.Add("Exit", null, delegate { Quit(); });
        tray.ContextMenuStrip = menu;
        Shown += delegate {
            if (startupError != null) { SetStatus("Configuration could not be loaded: " + startupError); return; }
            if (start) SaveAndApply(true);
        };
        FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideInBackground(); } };
        signals.Interval = 500;
        signals.Tick += delegate {
            if (Program.ShowSignal.WaitOne(0)) ShowPanel();
            if (Program.StartSignal.WaitOne(0)) SaveAndApply(true);
        };
        signals.Start();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ResumeLayout(true);
        Task.Run(() => Watch(cancellation.Token));
    }
    private static void AddLabel(TableLayoutPanel layout, string text, int row, bool title)
    {
        var label = new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, title ? 0 : 10, 0, 3) };
        if (title) label.Font = new Font(label.Font.FontFamily, 16, FontStyle.Bold);
        layout.Controls.Add(label, 0, row); layout.SetColumnSpan(label, 3);
    }
    private void AddFileRow(TableLayoutPanel layout, TextBox box, int row, string title, string filter, bool exe)
    {
        box.Dock = DockStyle.Fill;
        layout.Controls.Add(box, 0, row); layout.SetColumnSpan(box, 2);
        var button = new Button { Text = "Browse...", AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8, 3, 8, 3) };
        layout.Controls.Add(button, 2, row);
        button.Click += delegate {
            using (var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true })
            {
                if (File.Exists(box.Text)) dialog.FileName = box.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName;
            }
        };
    }
    private static void AddSlider(TableLayoutPanel layout, TrackBar slider, Label value, int row, int min, int max, int current, string name)
    {
        slider.Minimum = min; slider.Maximum = max; slider.Value = current; slider.TickFrequency = 10;
        slider.LargeChange = 10; slider.SmallChange = 1; slider.Dock = DockStyle.Fill; slider.Height = 38; slider.AccessibleName = name;
        value.AutoSize = true; value.Dock = DockStyle.Fill; value.TextAlign = ContentAlignment.MiddleCenter; value.Text = current + "%";
        slider.ValueChanged += delegate { value.Text = slider.Value + "%"; };
        layout.Controls.Add(slider, 0, row); layout.SetColumnSpan(slider, 2); layout.Controls.Add(value, 2, row);
    }
    private static void AddButton(FlowLayoutPanel panel, string text, Action click)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 0, 8, 6) };
        button.Click += delegate { click(); }; panel.Controls.Add(button);
    }
    private void SetStatus(string text)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { BeginInvoke((Action)(() => SetStatus(text))); return; }
        status.Text = text;
    }
    private void SaveAndApply(bool launch)
    {
        try
        {
            var next = new Settings { WallpaperPath = wallpaper.Text.Trim(), ApplicationPath = applicationPath.Text.Trim(), Brightness = brightness.Value, SidebarTransparency = transparency.Value, TextMaskStrength = textMask.Value, Enabled = true };
            next.Validate(true);
            if (launch && !File.Exists(next.ApplicationPath)) throw new Exception("Antigravity.exe not found. Select the installed application.");
            changing = true;
            Program.Save(next); settings = next; startupError = null;
            if (launch)
            {
                bool running = Process.GetProcessesByName("Antigravity").Any(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } });
                if (!running) Process.Start(new ProcessStartInfo(next.ApplicationPath) { UseShellExecute = true });
            }
            SetStatus("Settings saved. Applying wallpaper. Close hides the window and tray icon; Minimize to tray keeps an icon.");
        }
        catch (Exception error) { SetStatus("Could not save or apply settings: " + error.Message); }
        finally { changing = false; }
    }
    private void DisableWallpaper()
    {
        try { var next = Program.Load(); next.Enabled = false; Program.Save(next); settings = next; SetStatus("Restoring the default background. Your wallpaper settings are preserved."); }
        catch (Exception error) { SetStatus("Could not restore the default background: " + error.Message); }
    }
    private async Task Watch(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!changing)
                {
                    if (startupError != null) { await Task.Delay(2000, token).ConfigureAwait(false); continue; }
                    Settings current = File.Exists(Program.ConfigPath) ? Program.Load() : settings;
                    string result = await Injector.Apply(current, token).ConfigureAwait(false);
                    SetStatus(current.Enabled ? "Applied · Brightness " + current.Brightness + "% · Sidebar transparency " + current.SidebarTransparency + "% · Text mask " + current.TextMaskStrength + "% · Auto-apply enabled" : "Default background restored. Click Save & Apply to enable the wallpaper again.");
                    if (result.Contains("\"applied\":false")) SetStatus("Waiting for the Antigravity interface to finish loading.");
                }
            }
            catch (OperationCanceledException) { if (token.IsCancellationRequested) break; SetStatus("Antigravity connection timed out. Reconnecting automatically."); }
            catch (Exception error) { SetStatus("Preview connection unavailable. Retrying automatically. " + error.Message); }
            try { await Task.Delay(2500, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }
    private void MinimizeToTray() { tray.Visible = true; Hide(); }
    private void HideInBackground() { tray.Visible = false; Hide(); }
    private void ShowPanel() { tray.Visible = false; Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void Quit()
    {
        exiting = true; cancellation.Cancel(); tray.Visible = false; tray.Dispose(); Close();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { cancellation.Cancel(); signals.Dispose(); tray.Dispose(); }
        base.Dispose(disposing);
    }
}
