using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Nashi.Core;
using PdfSharp.Pdf.IO;
namespace Nashi.Windows;

/// The Windows side of the macOS HTMLToPDFTests: WebView2 must print browser layout, script-drawn codes,
/// local assets and undeclared UTF-8 without the office runtime, read nothing outside the page's folder,
/// never contact the network, and publish nothing when cancelled or over the page limit.
internal static class HtmlPdfChecks
{
    internal static void Run(string root,Action<bool,string> check)
    {
        check(FeatureCatalog.Formats(["page.html"]).SequenceEqual(["pdf"])&&FeatureCatalog.Formats(["page.HTM","other.html"]).SequenceEqual(["pdf"])&&FeatureCatalog.Tools(["page.html"]).Length==0,"HTML inputs do not offer exactly PDF");
        check(HtmlPdfRenderer.Available,"The WebView2 Runtime is unavailable for HTML printing");
        var parent=Directory.CreateDirectory(Path.Combine(root,"HTML to PDF "+Guid.NewGuid().ToString("N")[..8])).FullName;var site=Directory.CreateDirectory(Path.Combine(parent,"Site")).FullName;Directory.CreateDirectory(Path.Combine(site,"assets"));
        var profiles=Profiles();
        // Neither office nor media tools exist here: WebView2 alone prints the page.
        var conversion=new DocumentConversion(new OfficeRuntime(Path.Combine(parent,"No office")),new Toolchain(Path.Combine(parent,"No tools")));
        IReadOnlyList<string> Convert(string input,CancellationToken token=default)=>Wait(conversion.ConvertAsync([input],"pdf",Path.GetDirectoryName(input)!,new(),null,token));

        Png(Path.Combine(site,"assets","orange.png"),255,128,0);
        File.WriteAllText(Path.Combine(site,"site.css"),"header { display: flex; justify-content: space-between } .box { width: 80px; height: 80px; background: #00C8C8 }");
        // Without a charset declaration, WebView2 alone would read these bytes in the system code page.
        var input=Path.Combine(site,"Fatura.html");
        File.WriteAllBytes(input,Encoding.UTF8.GetBytes("""
        <!doctype html><html><head><title>Fatura Şişli</title><link rel="stylesheet" href="site.css"><script>document.write('Script ran')</script></head>
        <body style="margin:32px"><header><span>Left column</span><span>Right column</span></header><p>Merhaba Şişli ğüşıöç İ</p>
        <img src="assets/orange.png"><div class="box"></div><table style="width:100%;border-collapse:collapse"><tr><td style="border:1px solid">Ürün</td><td style="border:1px solid">Adet</td></tr></table>
        <div id="code"></div><script>
        // Like the QR code libraries in e-invoices: draw on a canvas, then swap in an image after an asynchronous check.
        const canvas = document.createElement('canvas'); canvas.width = canvas.height = 80;
        const context = canvas.getContext('2d'); context.fillStyle = '#00A000'; context.fillRect(0, 0, 80, 80);
        const image = new Image(); image.style.display = 'none'; document.getElementById('code').append(canvas, image);
        const check = new Image(); check.onload = () => { image.src = canvas.toDataURL('image/png'); image.style.display = 'block'; canvas.style.display = 'none' };
        check.src = 'data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7';
        </script>
        <p><a href="next.html">Next page</a> <a href="https://example.com/terms">Terms</a></p><p style="page-break-before:always">Second page</p></body></html>
        """));
        var original=File.ReadAllBytes(input);var output=Convert(input).Single();
        check(output==Path.Combine(site,"Fatura.pdf"),"HTML PDF was not named after its page: "+output);
        using(var pdf=UglyToad.PdfPig.PdfDocument.Open(output))
        {
            check(pdf.NumberOfPages==2,"HTML PDF page count: "+pdf.NumberOfPages);var text=Text(pdf);
            foreach(var expected in new[]{"Merhaba Şişli ğüşıöç İ","Ürün","Second page","Script ran"})check(text.Contains(expected,StringComparison.Ordinal),"HTML PDF is missing \""+expected+"\": "+text);
            check(pdf.Information.Title=="Fatura Şişli","HTML PDF title: "+pdf.Information.Title);
            // Flex layout keeps both labels on one line, at opposite page edges.
            var page=pdf.GetPage(1);var words=page.GetWords().ToArray();var left=words.First(w=>w.Text=="Left");var right=words.SkipWhile(w=>w.Text!="Right").Skip(1).First();
            check(left.BoundingBox.Left<page.Width/4&&right.BoundingBox.Right>page.Width*3/4&&Math.Abs(left.BoundingBox.Bottom-right.BoundingBox.Bottom)<2,"HTML PDF lost its flex layout");
        }
        check(Pixels(output,0,(255,128,0))>1000,"HTML PDF lost an image beside the page");check(Pixels(output,0,(0,200,200))>1000,"HTML PDF lost a stylesheet beside the page");check(Pixels(output,0,(0,160,0))>1000,"The script-drawn code is missing");
        // A link to a file beside the page cannot open from a shared PDF; web links stay.
        check(Links(output).SequenceEqual(["https://example.com/terms"]),"HTML PDF links: "+string.Join(", ",Links(output)));
        check(File.ReadAllBytes(input).SequenceEqual(original),"HTML printing changed the original page");

        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;var connections=0;
        _=Task.Run(async()=>{try{while(true){using var client=await listener.AcceptTcpClientAsync();Interlocked.Increment(ref connections);}}catch(Exception){}});
        using var receiver=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));var udpPort=((IPEndPoint)receiver.Client.LocalEndPoint!).Port;var datagrams=0;
        _=Task.Run(async()=>{try{while(true){await receiver.ReceiveAsync();Interlocked.Increment(ref datagrams);}}catch(Exception){}});
        var remote="http://127.0.0.1:"+port;
        // Control traffic proves the probe sees loopback TCP and UDP. HttpClient may retry its request.
        using(var http=new HttpClient{Timeout=TimeSpan.FromSeconds(3)})using(var sender=new UdpClient())
        {var request=http.GetAsync(remote+"/control");sender.Send("control"u8.ToArray(),new IPEndPoint(IPAddress.Loopback,udpPort));Until(()=>Volatile.Read(ref connections)>0&&Volatile.Read(ref datagrams)>0,10);Until(()=>request.IsCompleted,5);}
        check(connections>0&&datagrams>0,"The loopback probe saw no control traffic");Until(()=>false,.5);var control=(Volatile.Read(ref connections),Volatile.Read(ref datagrams));
        var outside=Path.Combine(parent,"outside.png");Png(outside,255,0,255);
        // Windows lets only developer mode or administrators create symbolic links.
        try{File.CreateSymbolicLink(Path.Combine(site,"assets","linked.png"),outside);}catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        // Directory junctions need no privilege, so any page folder may contain one.
        var elsewhere=Directory.CreateDirectory(Path.Combine(parent,"Elsewhere")).FullName;Png(Path.Combine(elsewhere,"elsewhere.png"),255,0,255);var junction=Path.Combine(site,"linked");
        using(var link=Process.Start(new ProcessStartInfo("cmd.exe"){ArgumentList={"/d","/c","mklink","/J",junction,elsewhere},CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true})!)link.WaitForExit(10000);
        check(File.Exists(Path.Combine(junction,"elsewhere.png")),"The directory junction fixture was not created");
        File.WriteAllText(Path.Combine(site,"secret.txt"),"Sibling secret marker");
        File.WriteAllText(Path.Combine(site,"local.css"),$"@import url('{remote}/nested.css'); p {{ background-image: url('{remote}/nested.png') }}");
        var probe=Path.Combine(site,"Probe.html");
        File.WriteAllText(probe,$$"""
        <!doctype html><html><head><meta charset="utf-8"><meta http-equiv="refresh" content="0;url={{remote}}/refresh">
        <link rel="stylesheet" href="{{remote}}/style.css"><link rel="stylesheet" href="local.css"><link rel="preconnect" href="{{remote}}"><link rel="dns-prefetch" href="{{remote}}"><link rel="prefetch" href="{{remote}}/prefetch">
        <style>@import url('{{remote}}/import.css'); @font-face { font-family: Remote; src: url('{{remote}}/font.woff2') } body { font-family: Remote; background: url('{{remote}}/background.png') }</style></head>
        <body><p>Offline probe</p><img src="{{remote}}/image.png"><img srcset="{{remote}}/srcset.png 1x"><img loading="lazy" src="{{remote}}/lazy.png">
        <video src="{{remote}}/video.mp4" poster="{{remote}}/poster.png" preload="auto" autoplay></video><iframe src="{{remote}}/frame"></iframe><object data="{{remote}}/object"></object>
        <svg width="20" height="20"><image href="{{remote}}/svg.png" width="20" height="20"/></svg><a ping="{{remote}}/ping" href="#top">Top</a>
        <img src="../outside.png"><img src="{{new Uri(outside).AbsoluteUri}}"><img src="assets/linked.png"><img src="linked/elsewhere.png"><iframe src="../outside.png"></iframe><img src="secret.txt:Zone.Identifier">
        <pre id="report"></pre><script>
        const report = line => { document.getElementById('report').textContent += line + '\n' };
        const frame = document.body.appendChild(document.createElement('iframe'));
        report('peer ' + typeof RTCPeerConnection + ', frame peer ' + typeof frame.contentWindow.RTCPeerConnection + ', camera ' + typeof navigator.mediaDevices + ', location ' + typeof navigator.geolocation);
        fetch('{{remote}}/fetch').catch(() => {}); try { navigator.sendBeacon('{{remote}}/beacon', 'x') } catch {}
        // Files beside the page may be shown by markup, but scripts may not read them into the PDF.
        fetch('secret.txt').then(response => response.text()).then(report, () => {});
        const sibling = new XMLHttpRequest(); sibling.open('GET', 'secret.txt', false); try { sibling.send(); report(sibling.responseText) } catch {}
        const request = new XMLHttpRequest(); request.open('GET', '{{remote}}/xhr'); try { request.send() } catch {}
        try { new WebSocket('ws://127.0.0.1:{{port}}/socket') } catch {}
        try { new EventSource('{{remote}}/events') } catch {}
        new Image().src = '{{remote}}/script-image'; import('{{remote}}/module.js').catch(() => {});
        try { new Worker(URL.createObjectURL(new Blob(["fetch('{{remote}}/worker').catch(() => {})"]))) } catch {}
        window.open('{{remote}}/popup'); setTimeout(() => { location.href = '{{remote}}/navigate' }, 10);
        const Peer = window.RTCPeerConnection || frame.contentWindow.RTCPeerConnection;
        if (Peer) { const peer = new Peer({ iceServers: [{ urls: 'stun:127.0.0.1:{{udpPort}}' }] }); peer.createDataChannel('x'); peer.createOffer().then(offer => peer.setLocalDescription(offer)) }
        </script></body></html>
        """);
        var probeOutput=Convert(probe).Single();Until(()=>false,1);
        check(Volatile.Read(ref connections)==control.Item1,"HTML printing opened a network connection");
        check(Volatile.Read(ref datagrams)==control.Item2,"HTML printing sent a datagram, as WebRTC does");
        using(var pdf=UglyToad.PdfPig.PdfDocument.Open(probeOutput))
        {
            var text=Text(pdf);check(text.Contains("Offline probe",StringComparison.Ordinal),"Offline probe text missing: "+text);
            check(text.Contains("peer undefined, frame peer undefined, camera undefined, location undefined",StringComparison.Ordinal),"WebRTC or device APIs stayed available: "+text);
            check(!text.Contains("Sibling secret marker",StringComparison.Ordinal),"A page script read a file beside the page");
            check(pdf.Information.Title=="Probe","Untitled HTML PDF was not named after its file: "+pdf.Information.Title);
            for(var n=0;n<pdf.NumberOfPages;n++)check(Pixels(probeOutput,n,(255,0,255))==0,"A file outside the page's folder was rendered");
        }
        var bytes=Encoding.Latin1.GetString(File.ReadAllBytes(probeOutput));check(!bytes.Contains("file:",StringComparison.Ordinal)&&!bytes.Contains("nashi-html",StringComparison.OrdinalIgnoreCase),"HTML PDF names a local file or Nashi's private origin");

        var limits=Directory.CreateDirectory(Path.Combine(parent,"Limits")).FullName;
        var longPage=Path.Combine(limits,"Long.html");File.WriteAllText(longPage,"<!doctype html><meta charset=\"utf-8\">"+string.Concat(Enumerable.Repeat("<p>"+string.Concat(Enumerable.Repeat("Nashi cancellation fixture. ",60))+"</p>",600)));
        using(var cancellation=new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {try{Convert(longPage,cancellation.Token);check(false,"Cancelled HTML printing published a PDF");}catch(OperationCanceledException){check(true,"HTML printing honoured cancellation");}}
        var tall=Path.Combine(limits,"Tall.html");File.WriteAllText(tall,"<!doctype html><p>Top</p><div style=\"height:10000000px\"></div>");
        try{Convert(tall);check(false,"A 10,000,000-pixel page printed");}catch(InvalidDataException error){check(error.Message==HtmlPdfRenderer.TooManyPages,"Tall page error: "+error.Message);}
        // A short page can still force more than 1,000 pages with page breaks.
        var breaks=Path.Combine(limits,"Breaks.html");File.WriteAllText(breaks,"<!doctype html>"+string.Concat(Enumerable.Repeat("<p style=\"break-before:page\">Page</p><p style=\"page-break-before:always\">Page</p>",520)));
        try{Convert(breaks);check(false,"A page with 1,040 forced breaks printed");}catch(InvalidDataException error){check(error.Message==HtmlPdfRenderer.TooManyPages,"Forced break error: "+error.Message);}
        Until(()=>false,2);
        check(Directory.GetFileSystemEntries(limits).Select(Path.GetFileName).Order(StringComparer.Ordinal).SequenceEqual(["Breaks.html","Long.html","Tall.html"]),"Failed HTML printing left files behind: "+string.Join(", ",Directory.GetFileSystemEntries(limits).Select(Path.GetFileName)));
        check(Profiles().SetEquals(profiles),"HTML printing left a WebView2 profile behind");
    }

    private static T Wait<T>(Task<T> task){var clock=Stopwatch.StartNew();while(!task.IsCompleted){if(clock.Elapsed.TotalSeconds>180)throw new TimeoutException("HTML printing check timed out.");DesktopInput.Pump(30);}return task.GetAwaiter().GetResult();}
    private static void Until(Func<bool> condition,double seconds){var clock=Stopwatch.StartNew();while(!condition()&&clock.Elapsed.TotalSeconds<seconds)DesktopInput.Pump(30);}
    private static HashSet<string> Profiles()=>Directory.GetDirectories(Path.GetTempPath(),"Nashi HTML *").ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static string Text(UglyToad.PdfPig.PdfDocument pdf)=>string.Join("\n",pdf.GetPages().Select(page=>string.Join(" ",page.GetWords().Select(w=>w.Text))));
    private static List<string> Links(string pdf)
    {
        using var document=PdfReader.Open(pdf,PdfDocumentOpenMode.Import);var links=new List<string>();
        foreach(var page in document.Pages)for(var n=0;n<page.Annotations.Count;n++)if(page.Annotations[n].Elements.GetDictionary("/A")?.Elements.GetString("/URI") is {} uri)links.Add(uri);return links;
    }
    private static void Png(string path,byte red,byte green,byte blue)
    {
        var pixels=new byte[80*80*4];for(var n=0;n<pixels.Length;n+=4){pixels[n]=blue;pixels[n+1]=green;pixels[n+2]=red;pixels[n+3]=255;}
        ImageEngine.Save(BitmapSource.Create(80,80,96,96,PixelFormats.Bgra32,null,pixels,80*4),path,"png");
    }
    private static int Pixels(string pdf,int page,(int Red,int Green,int Blue) color)
    {
        using var document=PdfRasterDocument.Open(pdf);var image=new FormatConvertedBitmap(ImageEngine.FromPng(document.RenderPage(page,72,CancellationToken.None)),PixelFormats.Bgra32,null,0);
        var bytes=new byte[image.PixelWidth*image.PixelHeight*4];image.CopyPixels(bytes,image.PixelWidth*4,0);var count=0;
        for(var n=0;n<bytes.Length;n+=4)if(Math.Abs(bytes[n+2]-color.Red)<40&&Math.Abs(bytes[n+1]-color.Green)<40&&Math.Abs(bytes[n]-color.Blue)<40)count++;return count;
    }
}
