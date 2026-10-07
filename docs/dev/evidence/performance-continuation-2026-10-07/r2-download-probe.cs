using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;
const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
var root = Path.Combine(Path.GetTempPath(), "DropSpace-download-lifetime-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
var endpoint = (IPEndPoint)server.LocalEndpoint;
using var serverStop = new CancellationTokenSource();
var serve = Task.Run(async () => {
 try { while (true) {
  using var client = await server.AcceptTcpClientAsync(serverStop.Token);
  await using var stream = client.GetStream();
  using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen:true);
  while (!string.IsNullOrEmpty(await reader.ReadLineAsync(serverStop.Token))) { }
  await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nConnection: close\r\n\r\n"), serverStop.Token);
  await stream.WriteAsync(new byte[]{1,2,3}, serverStop.Token);
 } } catch (OperationCanceledException) when (serverStop.IsCancellationRequested) { }
});
using var engine = new HttpRangeDownloader();
await using var manager = new DownloadManager(engine, new DownloadTaskRepository(Path.Combine(root,"journal")));
var weak = new List<WeakReference>();
var capturedNonNullAtStart = 0;
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
for (int i=0;i<100;i++) {
 await manager.EnqueueAsync($"http://127.0.0.1:{endpoint.Port}/file", root, $"file-{i}.bin");
 var id = manager.Tasks.Last().Id;
 var work = ((IDictionary)typeof(DownloadManager).GetField("_work", Fields)!.GetValue(manager)!)[id]!;
 var source = work.GetType().GetField("Stop", Fields)!.GetValue(work);
 weak.Add(new WeakReference(source));
 if (source is not null) capturedNonNullAtStart++;
 await ((Task)work.GetType().GetField("Run", Fields)!.GetValue(work)!).WaitAsync(TimeSpan.FromSeconds(10));
 if (manager.Tasks.Last().State != DownloadTaskState.Completed) throw new InvalidOperationException(manager.Tasks.Last().ErrorCode);
}
stopwatch.Stop();
var liveStops = 0; var disposedStops = 0;
foreach (var work in ((IDictionary)typeof(DownloadManager).GetField("_work",Fields)!.GetValue(manager)!).Values) {
 if (work!.GetType().GetField("Stop",Fields)!.GetValue(work) is CancellationTokenSource source) {
  liveStops++;
  if ((bool)typeof(CancellationTokenSource).GetField("_disposed",Fields)!.GetValue(source)!) disposedStops++;
 }
}
var parent = (CancellationTokenSource)typeof(DownloadManager).GetField("_lifetime",Fields)!.GetValue(manager)!;
var registrations = typeof(CancellationTokenSource).GetField("_registrations",Fields)!.GetValue(parent);
var callback = registrations?.GetType().GetField("Callbacks",Fields)!.GetValue(registrations);
var links = 0;
while (callback != null) { links++; callback = callback.GetType().GetField("Next",Fields)!.GetValue(callback); }
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
Console.WriteLine(JsonSerializer.Serialize(new { Downloads=manager.Tasks.Count,HistoryCompleted=manager.Tasks.Count(x=>x.State==DownloadTaskState.Completed), RetainedRunSources=liveStops, DisposedRetainedSources=disposedStops, LifetimeActiveCallbacks=links, CapturedNonNullAtStart=capturedNonNullAtStart, CapturedSourcesAlive=weak.Count(x=>x.IsAlive), WallMilliseconds=stopwatch.Elapsed.TotalMilliseconds }, new JsonSerializerOptions{WriteIndented=true}));
await manager.DisposeAsync();
serverStop.Cancel(); await serve;
Directory.Delete(root,true);
