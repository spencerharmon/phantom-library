using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

internal sealed class StubKeyProvider : ITmdbApiKeyProvider
{
    private readonly string _key;
    private readonly string _baseUrl;
    public StubKeyProvider(string key, string baseUrl = "https://api.themoviedb.org/3")
    {
        _key = key;
        _baseUrl = baseUrl;
    }
    public string GetApiKey() => _key;
    public string GetBaseUrl() => _baseUrl;
}

internal sealed class QueuedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public QueuedHandler Enqueue(HttpStatusCode status, string? body = null, Action<HttpResponseMessage>? mutate = null)
    {
        _responses.Enqueue(req =>
        {
            var msg = new HttpResponseMessage(status);
            if (body is not null)
            {
                msg.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            }

            mutate?.Invoke(msg);
            return msg;
        });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("QueuedHandler ran out of canned responses");
        }

        var producer = _responses.Dequeue();
        return Task.FromResult(producer(request));
    }
}
