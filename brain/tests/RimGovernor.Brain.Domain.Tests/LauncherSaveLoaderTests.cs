using System.Net;
using RimGovernor.Launcher;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

public sealed class LauncherSaveLoaderTests
{
    [Fact]
    public void RetriesAndPollsAreShort()
    {
        using var loader = new SaveLoader(new HttpClient());

        Assert.True(loader.RetryDelay <= TimeSpan.FromMilliseconds(250), $"retry delay {loader.RetryDelay}");
    }

    [Fact]
    public async Task LoadIsPostedOnTheFirstRetryAfterTheRoutesAppear()
    {
        // The channel answers 404 until the brain registers /api/lifecycle; the routes appear right after the third 404.
        var game = new FakeGame(notFoundPosts: 3, inProgressPolls: 2);
        using var loader = new SaveLoader(new HttpClient(game)) { RetryDelay = TimeSpan.FromMilliseconds(1) };

        await loader.LoadAsync("http://game/", "colony", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(["POST 404", "POST 404", "POST 404", "POST 202", "GET 202", "GET 202", "GET 200"], game.Log);
    }

    private sealed class FakeGame(int notFoundPosts, int inProgressPolls) : HttpMessageHandler
    {
        private int _posts;
        private int _polls;

        public List<string> Log { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpStatusCode status;
            if (request.Method == HttpMethod.Post)
            {
                status = _posts++ < notFoundPosts ? HttpStatusCode.NotFound : HttpStatusCode.Accepted;
            }
            else
            {
                status = _polls++ < inProgressPolls ? HttpStatusCode.Accepted : HttpStatusCode.OK;
            }

            Log.Add($"{request.Method} {(int)status}");
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
