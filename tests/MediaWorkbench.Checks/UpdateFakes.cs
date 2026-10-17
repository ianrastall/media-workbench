using System.Net;
using MediaWorkbench.Core;

/// <summary>A scripted update source for view-model checks.</summary>
sealed class FakeUpdates(UpdateInfo? available) : IUpdateService
{
    public string CurrentVersion => "0.2.0-alpha.2";
    public int Checks { get; private set; }
    public bool Offline { get; set; }
    public Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        Checks++;
        return Offline ? throw new HttpRequestException("offline") : Task.FromResult(available);
    }
    public Task<string> DownloadAsync(UpdateInfo update, IProgress<OperationProgress> progress, CancellationToken cancellationToken) =>
        Task.FromResult(@"C:\verified\MediaWorkbench-" + update.Version + "-win-x64-setup.exe");
}

/// <summary>Serves fixed bodies by URL, so release checks and downloads run without a network.</summary>
sealed class StubHttp(Dictionary<string, byte[]> bodies) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(bodies.TryGetValue(request.RequestUri!.ToString(), out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
}
