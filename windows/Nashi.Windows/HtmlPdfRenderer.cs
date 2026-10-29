using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Nashi.Core;
using PdfSharp.Pdf.IO;
namespace Nashi.Windows;

/// Prints a local HTML file with the system WebView2 (Microsoft Edge) engine, as macOS prints with
/// WebKit, so the PDF keeps the layout a browser shows. Page scripts run without network or device
/// access, WebView2 receives only regular files from the page's own folder, and every other load is
/// refused before it leaves Nashi.
internal static class HtmlPdfRenderer
{
    internal const string Origin="https://nashi-html.invalid/";
    internal const string TooManyPages="This HTML file would need more than 1,000 PDF pages.";
    private const string PrintFailed="WebView2 could not print this HTML file to PDF.";
    // Request interception does not see every connection Chromium opens. With no proxy and no host that
    // resolves, nothing the page starts can connect around it, and background browser services stay off.
    private const string Arguments="--no-proxy-server --host-resolver-rules=\"MAP * ~NOTFOUND\" --disable-background-networking --disable-component-update --no-pings";
    // Markup may show files beside the page, but scripts may not read them (connect-src) or load anything
    // else. Every response carries the policy, and frames without their own response inherit it.
    private const string Policy="default-src 'self' data: blob:; script-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; style-src 'self' 'unsafe-inline' data:; connect-src 'none'; worker-src 'none'; object-src 'none'; frame-src 'self'; form-action 'none'";
    // Page scripts run because documents such as e-invoices draw their QR codes with them. Before the first
    // one runs they lose WebRTC, whose UDP neither interception nor browser switches stop, and the camera,
    // microphone and location.
    private const string WithoutDevices="for (const name of ['RTCPeerConnection', 'webkitRTCPeerConnection', 'RTCDataChannel', 'RTCIceCandidate', 'RTCSessionDescription', 'speechSynthesis']) delete window[name]; for (const name of ['mediaDevices', 'geolocation', 'getUserMedia', 'webkitGetUserMedia']) delete Navigator.prototype[name]";
    // Lazy images and web fonts must be ready. Scripts may swap in an image, such as a QR code, just after
    // loading, so images are checked again after a short pause.
    private const string Ready="(async () => { const settled = () => Promise.all([...document.images].map(image => image.complete || new Promise(done => { image.addEventListener('load', done, { once: true }); image.addEventListener('error', done, { once: true }) }))); for (const image of document.images) image.loading = 'eager'; await settled(); await document.fonts.ready; await new Promise(done => setTimeout(done, 100)); await settled(); return { height: document.documentElement.scrollHeight, title: document.title } })()";

    public static bool Available{get{try{return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());}catch(WebView2RuntimeNotFoundException){return false;}}}

    public static async Task RenderAsync(string input,string output,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // WebView2 needs a single-threaded apartment with a message loop of its own, whichever thread converts.
        var started=new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{started.SetResult(Dispatcher.CurrentDispatcher);Dispatcher.Run();}){IsBackground=true,Name="Nashi HTML printing"};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        var dispatcher=await started.Task;
        try{await dispatcher.InvokeAsync(()=>new Job(input,output).RunAsync(token)).Task.Unwrap();}
        finally{dispatcher.InvokeShutdown();}
    }

    /// WebView2 reads an undeclared page in the system code page, but such files are nearly always UTF-8.
    internal static string? Charset(byte[] html)
    {
        if(Regex.IsMatch(Encoding.UTF8.GetString(html,0,Math.Min(html.Length,1024)),"<meta[^>]*charset",RegexOptions.IgnoreCase,TimeSpan.FromSeconds(1)))return null;
        try{_=new UTF8Encoding(false,true).GetCharCount(html);return "utf-8";}catch(DecoderFallbackException){return null;}
    }

    private sealed class Job(string input,string output)
    {
        private readonly string folder=Path.GetDirectoryName(Path.GetFullPath(input))!;
        private readonly DateTime deadline=DateTime.UtcNow.AddSeconds(120);
        private readonly TaskCompletionSource<string> stopped=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CoreWebView2Environment? environment;
        private ulong navigation;

        public async Task RunAsync(CancellationToken token)
        {
            var profile=Path.Combine(Path.GetTempPath(),"Nashi HTML "+Guid.NewGuid().ToString("N"));var exited=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            HwndSource? window=null;Task<CoreWebView2Controller>? creating=null;CoreWebView2Controller? controller=null;
            var (paperWidth,paperHeight)=Paper();var width=(int)Math.Round((paperWidth-1)*96);var height=(int)Math.Round((paperHeight-1)*96);
            try
            {
                environment=await Within(CoreWebView2Environment.CreateAsync(null,profile,new CoreWebView2EnvironmentOptions(Arguments){ExclusiveUserDataFolderAccess=true,AreBrowserExtensionsEnabled=false}),token);environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult();
                // The view never shows; its window only hosts layout at the printable width.
                window=new HwndSource(new HwndSourceParameters("Nashi HTML",width,height){WindowStyle=unchecked((int)0x80000000),ExtendedWindowStyle=0x08000080});
                var options=environment.CreateCoreWebView2ControllerOptions();options.IsInPrivateModeEnabled=true;
                creating=environment.CreateCoreWebView2ControllerAsync(window.Handle,options);controller=await Within(creating,token);
                controller.ShouldDetectMonitorScaleChanges=false;controller.RasterizationScale=1;controller.Bounds=new(0,0,width,height);
                var view=controller.CoreWebView2;var loaded=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);Configure(view,loaded);
                await Within(view.AddScriptToExecuteOnDocumentCreatedAsync(WithoutDevices),token);
                view.Navigate(Origin+Uri.EscapeDataString(Path.GetFileName(input)));
                if(await Within(loaded.Task,token) is {} error)throw new InvalidDataException("The HTML file could not be opened. "+error);
                // Measure in a world of Nashi's own, so page scripts cannot change what it reads.
                using var tree=JsonDocument.Parse(await Within(view.CallDevToolsProtocolMethodAsync("Page.getFrameTree","{}"),token));var frame=tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
                using var world=JsonDocument.Parse(await Within(view.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",JsonSerializer.Serialize(new{frameId=frame,worldName="Nashi"})),token));var context=world.RootElement.GetProperty("executionContextId").GetInt32();
                using var measured=JsonDocument.Parse(await Within(view.CallDevToolsProtocolMethodAsync("Runtime.evaluate",JsonSerializer.Serialize(new{expression=Ready,contextId=context,awaitPromise=true,returnByValue=true})),token));
                var page=measured.RootElement.GetProperty("result").TryGetProperty("value",out var value)&&value.ValueKind==JsonValueKind.Object?value:default;
                // CSS pixels print at three quarters of a point.
                if((page.ValueKind==JsonValueKind.Object&&page.TryGetProperty("height",out var content)&&content.TryGetDouble(out var pixels)?pixels:0)*.75/((paperHeight-1)*72)>1000)throw new InvalidDataException(TooManyPages);
                var print=environment.CreatePrintSettings();print.Orientation=CoreWebView2PrintOrientation.Portrait;print.PageWidth=paperWidth;print.PageHeight=paperHeight;print.MarginTop=print.MarginBottom=print.MarginLeft=print.MarginRight=.5;print.ScaleFactor=1;print.ShouldPrintBackgrounds=true;print.ShouldPrintHeaderAndFooter=false;
                if(!await Within(view.PrintToPdfAsync(output,print),token)||!File.Exists(output))throw new InvalidOperationException(PrintFailed);
                Finish(page.ValueKind==JsonValueKind.Object&&page.TryGetProperty("title",out var title)?title.GetString()??"":"");
            }
            finally
            {
                if(controller is null&&creating is not null){await Task.WhenAny(creating,Task.Delay(5000));if(creating.IsCompletedSuccessfully)controller=creating.Result;}
                controller?.Close();window?.Dispose();
                // The browser process may still be writing; its output and profile are only safe to remove once it exits.
                if(controller is not null)await Task.WhenAny(exited.Task,Task.Delay(5000));
                for(var attempt=0;attempt<20&&Directory.Exists(profile);attempt++){try{Directory.Delete(profile,true);}catch(IOException){await Task.Delay(100);}catch(UnauthorizedAccessException){await Task.Delay(100);}}
            }
        }

        private void Configure(CoreWebView2 view,TaskCompletionSource<string?> loaded)
        {
            var settings=view.Settings;settings.IsScriptEnabled=true;settings.AreDefaultScriptDialogsEnabled=false;settings.IsWebMessageEnabled=false;settings.AreHostObjectsAllowed=false;settings.AreDevToolsEnabled=false;settings.AreDefaultContextMenusEnabled=false;settings.AreBrowserAcceleratorKeysEnabled=false;settings.IsBuiltInErrorPageEnabled=false;settings.IsStatusBarEnabled=false;settings.IsZoomControlEnabled=false;settings.IsPasswordAutosaveEnabled=false;settings.IsGeneralAutofillEnabled=false;settings.IsReputationCheckingRequired=false;
            // A dark Windows theme must not switch the page to its dark color scheme.
            view.Profile.PreferredColorScheme=CoreWebView2PreferredColorScheme.Light;
            // Nashi serves the page and its files itself, so WebView2 gets no read access to the disk.
            view.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);view.WebResourceRequested+=Serve;
            // Frames may show files beside the page. Nothing navigates away from it.
            view.NavigationStarting+=(_,e)=>{e.Cancel=!IsPage(e.Uri);if(!e.Cancel&&navigation==0)navigation=e.NavigationId;};
            view.FrameNavigationStarting+=(_,e)=>e.Cancel=!e.Uri.StartsWith(Origin,StringComparison.OrdinalIgnoreCase)&&!e.Uri.StartsWith("about:",StringComparison.OrdinalIgnoreCase);
            view.NavigationCompleted+=(_,e)=>{if(e.NavigationId==navigation)loaded.TrySetResult(e.IsSuccess?null:e.HttpStatusCode is >=400 and var status?"HTTP "+status:e.WebErrorStatus.ToString());};
            view.NewWindowRequested+=(_,e)=>e.Handled=true;view.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;view.DownloadStarting+=(_,e)=>e.Cancel=true;view.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
            view.ProcessFailed+=(_,e)=>{if(e.ProcessFailedKind is CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)stopped.TrySetResult("WebView2 stopped while rendering the HTML file. No PDF was exported.");};
        }

        private bool IsPage(string uri)=>uri.StartsWith(Origin,StringComparison.OrdinalIgnoreCase)&&Uri.TryCreate(uri,UriKind.Absolute,out var url)&&Uri.UnescapeDataString(url.AbsolutePath)=="/"+Path.GetFileName(input);

        private void Serve(object? sender,CoreWebView2WebResourceRequestedEventArgs e)
        {
            var file=e.Request.Method=="GET"?Local(e.Request.Uri):null;byte[]? data=null;
            try{if(file is not null)data=File.ReadAllBytes(file);}catch(IOException){}catch(UnauthorizedAccessException){}
            if(file is null||data is null){e.Response=environment!.CreateWebResourceResponse(null,404,"Not Found","");return;}
            var type=FeatureCatalog.Extension(file)=="html"?"text/html"+(Charset(data) is {} charset?"; charset="+charset:""):Mime(file);
            e.Response=environment!.CreateWebResourceResponse(new MemoryStream(data),200,"OK","Content-Type: "+type+"\r\nContent-Security-Policy: "+Policy+"\r\nCache-Control: no-store");
        }

        /// The chosen page, or a regular file it links to inside its folder that no link leads to.
        private string? Local(string uri)
        {
            if(IsPage(uri))return input;
            if(!uri.StartsWith(Origin,StringComparison.OrdinalIgnoreCase)||!Uri.TryCreate(uri,UriKind.Absolute,out var url))return null;
            string file;try{file=SafePaths.ArchiveEntry(folder,Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/'));}catch(ArgumentException){return null;}catch(InvalidDataException){return null;}
            var info=new FileInfo(file);if(!info.Exists||info.Length>50_000_000)return null;
            try{for(var part=file;part.Length>folder.Length;part=Path.GetDirectoryName(part)!)if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)return null;}catch(IOException){return null;}
            return file;
        }

        private void Finish(string title)
        {
            using var pdf=PdfReader.Open(new MemoryStream(File.ReadAllBytes(output)),PdfDocumentOpenMode.Modify);
            if(pdf.PageCount==0)throw new InvalidOperationException(PrintFailed);
            // Forced page breaks add pages that the height estimate cannot see.
            if(pdf.PageCount>1000)throw new InvalidDataException(TooManyPages);
            // Links to files beside the page cannot open from a shared PDF, and an untitled page is named after its file.
            var changed=false;foreach(var page in pdf.Pages)for(var n=page.Annotations.Count-1;n>=0;n--)if(page.Annotations[n].Elements.GetDictionary("/A")?.Elements.GetString("/URI") is {} link&&link.StartsWith(Origin,StringComparison.OrdinalIgnoreCase)){page.Annotations.Remove(page.Annotations[n]);changed=true;}
            var name=string.IsNullOrWhiteSpace(title)?Path.GetFileNameWithoutExtension(input):title.Trim();if(pdf.Info.Title!=name){pdf.Info.Title=name;changed=true;}
            if(changed)pdf.Save(output);
        }

        private async Task<T> Within<T>(Task<T> work,CancellationToken token)
        {
            var remaining=deadline-DateTime.UtcNow;if(remaining>TimeSpan.Zero)await Task.WhenAny(work,stopped.Task,Task.Delay(remaining,token));
            token.ThrowIfCancellationRequested();if(stopped.Task.IsCompleted)throw new InvalidOperationException(stopped.Task.Result);
            if(!work.IsCompleted)throw new TimeoutException("The HTML file did not finish rendering. No PDF was exported.");return await work;
        }

        private static string Mime(string file)=>Path.GetExtension(file).ToLowerInvariant() switch{".css"=>"text/css",".js" or ".mjs"=>"text/javascript",".json"=>"application/json",".xhtml"=>"application/xhtml+xml",".xml"=>"application/xml",".txt"=>"text/plain",".svg"=>"image/svg+xml",".png"=>"image/png",".jpg" or ".jpeg"=>"image/jpeg",".gif"=>"image/gif",".webp"=>"image/webp",".avif"=>"image/avif",".bmp"=>"image/bmp",".ico"=>"image/x-icon",".woff"=>"font/woff",".woff2"=>"font/woff2",".ttf"=>"font/ttf",".otf"=>"font/otf",".mp4"=>"video/mp4",".webm"=>"video/webm",".mp3"=>"audio/mpeg",".wav"=>"audio/wav",_=>"application/octet-stream"};

        // The user's regional paper size, with the margins browsers print by default.
        private static (double Width,double Height) Paper()=>(GetLocaleInfoEx(null,0x2000100A,out var paper,2)>0?paper:9) switch{1=>(8.5,11),5=>(8.5,14),8=>(11.69,16.54),_=>(8.27,11.69)};
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern int GetLocaleInfoEx(string? locale,uint type,out int value,int length);
    }
}
