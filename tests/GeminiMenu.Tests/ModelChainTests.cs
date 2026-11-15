using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace GeminiMenu;

public class ModelChainTests
{
    static AppException Err(ErrKind kind, int status = 0, string? tech = null) => new(kind, "msg " + kind, status, tech);
    static ModelChain Chain(ModelHealth? h = null, List<string>? statuses = null) =>
        new(h ?? new ModelHealth(), "Gemini", statuses == null ? null : statuses.Add);

    static Task<GenResult> Ok(string model) => Task.FromResult(new GenResult("text from " + model, "STOP"));

    // ------------------------------------------------------------ classification

    [Theory]
    [InlineData(ErrKind.Overloaded)]
    [InlineData(ErrKind.Quota)]
    [InlineData(ErrKind.NotFound)]
    [InlineData(ErrKind.Network)]
    public void Classify_TransientAndMissingModelErrors_SwitchModel(ErrKind kind)
    {
        Assert.True(ModelFallback.ShouldSwitch(Err(kind)));
        Assert.False(ModelFallback.IsFatal(Err(kind)));
    }

    [Theory]
    [InlineData(ErrKind.Blocked)]
    [InlineData(ErrKind.BadRequest)]
    [InlineData(ErrKind.FileProblem)]
    [InlineData(ErrKind.Unsupported)]
    public void Classify_OtherErrors_DoNotSwitch(ErrKind kind) => Assert.False(ModelFallback.ShouldSwitch(Err(kind)));

    [Fact]
    public void Classify_MissingOrInvalidKey_IsFatal()
    {
        Assert.True(ModelFallback.IsFatal(Err(ErrKind.NoKey)));
        Assert.True(ModelFallback.IsFatal(Err(ErrKind.BadKey, 401)));
        Assert.True(ModelFallback.IsFatal(Err(ErrKind.BadKey, 400, "API key not valid")));
        Assert.False(ModelFallback.ShouldSwitch(Err(ErrKind.BadKey, 401)));
    }

    [Fact]
    public void Classify_403_IsAModelProblem_UnlessTheTextBlamesTheKey()
    {
        Assert.True(ModelFallback.ShouldSwitch(Err(ErrKind.BadKey, 403, "HTTP 403: model is not available for this project")));
        Assert.False(ModelFallback.IsFatal(Err(ErrKind.BadKey, 403, "HTTP 403: permission denied")));
        Assert.True(ModelFallback.IsFatal(Err(ErrKind.BadKey, 403, "HTTP 403: Your API key was reported as leaked")));
    }

    [Fact]
    public void ParseList_SplitsTrimsAndDropsDuplicates()
    {
        Assert.Equal(new[] { "a", "b", "c" }, ModelFallback.ParseList(" a, b;\nc\r\n a ,, "));
        Assert.Empty(ModelFallback.ParseList(null));
    }

    // ------------------------------------------------------------ chain

    [Fact]
    public async Task FirstModelWorks_NoSwitch_ResultNamesTheModel()
    {
        var calls = new List<string>();
        var r = await Chain().RunAsync(new[] { "a", "b" }, null, (c, _) => { calls.Add(c.Model); return Ok(c.Model); }, 4, default);
        Assert.Equal(new[] { "a" }, calls);
        Assert.Equal("a", r.Model);
    }

    [Theory]
    [InlineData(ErrKind.Overloaded)]
    [InlineData(ErrKind.Quota)]
    [InlineData(ErrKind.NotFound)]
    [InlineData(ErrKind.Network)]
    public async Task FailureThatJustifiesIt_MovesToTheNextModel(ErrKind kind)
    {
        var statuses = new List<string>();
        var r = await Chain(null, statuses).RunAsync(new[] { "a", "b" }, null,
            (c, _) => c.Model == "a" ? throw Err(kind) : Ok(c.Model), 4, default);
        Assert.Equal("b", r.Model);
        Assert.Contains(statuses, s => s.Contains("b"));
    }

    [Fact]
    public async Task ModelThatCrashesMidChunk_FallsBackToTheNext()
    {
        // The first model fails only after doing some work (timeout / dropped connection while waiting for the answer).
        int started = 0;
        var r = await Chain().RunAsync(new[] { "a", "b" }, null, async (c, _) =>
        {
            Interlocked.Increment(ref started);
            await Task.Yield();
            if (c.Model == "a") throw new AppException(ErrKind.Network, "timeout", technical: "timeout");
            return new GenResult("ok", "STOP");
        }, 4, default);
        Assert.Equal("b", r.Model);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task InvalidKey_FailsAtOnce_WithoutTryingOtherModels()
    {
        var calls = new List<string>();
        var ex = await Assert.ThrowsAsync<AppException>(() => Chain().RunAsync(new[] { "a", "b" }, null,
            (c, _) => { calls.Add(c.Model); throw Err(ErrKind.BadKey, 401); }, 4, default));
        Assert.Equal(ErrKind.BadKey, ex.Kind);
        Assert.Equal(new[] { "a" }, calls);
    }

    [Fact]
    public async Task ErrorsThatAreNotModelProblems_AreNotSwitchedAndDoNotCountForTheBreaker()
    {
        var health = new ModelHealth(1);
        var ex = await Assert.ThrowsAsync<AppException>(() => Chain(health).RunAsync(new[] { "a", "b" }, null,
            (c, _) => throw Err(ErrKind.Blocked), 4, default));
        Assert.Equal(ErrKind.Blocked, ex.Kind);
        Assert.False(health.IsDown("a"));
    }

    [Fact]
    public async Task AllModelsFail_ReportsTheLastErrorWithEveryModelInTheDetails()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Chain().RunAsync(new[] { "a", "b" }, null,
            (c, _) => throw Err(c.Model == "a" ? ErrKind.Quota : ErrKind.Overloaded, 0, "detail " + c.Model), 4, default));
        Assert.Equal(ErrKind.Overloaded, ex.Kind);
        Assert.Contains("[a] detail a", ex.Technical);
        Assert.Contains("[b] detail b", ex.Technical);
        Assert.Null(ex.Provider); // Gemini errors keep their dedicated error page
    }

    [Fact]
    public async Task Every403_EndsAsAKeyProblem()
    {
        var ex = await Assert.ThrowsAsync<AppException>(() => Chain().RunAsync(new[] { "a", "b" }, null,
            (c, _) => throw Err(ErrKind.BadKey, 403, "denied"), 4, default));
        Assert.Equal(ErrKind.BadKey, ex.Kind);
    }

    [Fact]
    public async Task OneModelWithout403_SavesTheTask()
    {
        var r = await Chain().RunAsync(new[] { "a", "b" }, null,
            (c, _) => c.Model == "a" ? throw Err(ErrKind.BadKey, 403, "model not allowed") : Ok(c.Model), 4, default);
        Assert.Equal("b", r.Model);
    }

    [Fact]
    public async Task Breaker_SkipsAModelAfterConsecutiveFailures_ForTheRestOfTheTask()
    {
        var health = new ModelHealth(3);
        var chain = Chain(health);
        var calls = new List<string>();
        Task<GenResult> Call(ModelCall c, CancellationToken _) { calls.Add(c.Model); return c.Model == "a" ? throw Err(ErrKind.Overloaded) : Ok(c.Model); }

        for (int i = 0; i < 3; i++) await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default);
        Assert.True(health.IsDown("a"));
        calls.Clear();

        var r = await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default);
        Assert.Equal("b", r.Model);
        Assert.Equal(new[] { "b" }, calls); // "a" is not even tried
    }

    [Fact]
    public async Task Breaker_AWorkingCallResetsTheStreak()
    {
        var health = new ModelHealth(3);
        var chain = Chain(health);
        int n = 0;
        Task<GenResult> Call(ModelCall c, CancellationToken _) => c.Model == "a" && ++n % 3 != 0 ? throw Err(ErrKind.Overloaded) : Ok(c.Model);
        for (int i = 0; i < 6; i++) await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default);
        Assert.False(health.IsDown("a")); // 2 failures, 1 success, 2 failures, 1 success: never 3 in a row
    }

    [Fact]
    public async Task AllModelsDown_FailsAtOnce_AndNamesThem()
    {
        var health = new ModelHealth(1);
        var chain = Chain(health);
        await Assert.ThrowsAsync<AppException>(() => chain.RunAsync(new[] { "a" }, null, (c, _) => throw Err(ErrKind.Overloaded, 0, "boom"), 4, default));
        int calls = 0;
        var ex = await Assert.ThrowsAsync<AppException>(() => chain.RunAsync(new[] { "a" }, null, (c, _) => { calls++; return Ok("x"); }, 4, default));
        Assert.Equal(0, calls);
        Assert.Equal(ErrKind.Overloaded, ex.Kind);
        Assert.Contains("[a] boom", ex.Technical);
    }

    sealed class FakeClock
    {
        public DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    [Fact]
    public void Breaker_TemporaryFailure_GetsOneProbeAfterThePause_NotBefore()
    {
        var clock = new FakeClock();
        var health = new ModelHealth(2, TimeSpan.FromSeconds(60), () => clock.Now);
        health.Failure("a", "429");
        Assert.True(health.Failure("a", "429"));
        Assert.True(health.IsDown("a"));

        clock.Advance(59);
        Assert.True(health.IsDown("a"));

        clock.Advance(2);
        Assert.False(health.IsDown("a")); // the probe
        Assert.True(health.IsDown("a"));  // the other chunks keep skipping it meanwhile
    }

    [Fact]
    public void Breaker_ProbeThatWorks_BringsTheModelBack()
    {
        var clock = new FakeClock();
        var health = new ModelHealth(1, TimeSpan.FromSeconds(60), () => clock.Now);
        health.Failure("a", "429");
        clock.Advance(61);
        Assert.False(health.IsDown("a"));
        health.Success("a");
        Assert.False(health.IsDown("a"));
        Assert.False(health.IsDown("a"));
        Assert.Empty(health.DownModels());
        Assert.True(health.Failure("a", "429")); // down again from scratch: a new marking
    }

    [Fact]
    public void Breaker_ProbeThatFails_StartsAnotherPause()
    {
        var clock = new FakeClock();
        var health = new ModelHealth(1, TimeSpan.FromSeconds(60), () => clock.Now);
        health.Failure("a", "429");
        clock.Advance(61);
        Assert.False(health.IsDown("a"));
        Assert.False(health.Failure("a", "429")); // not a new marking: it was already on the list
        clock.Advance(30);
        Assert.True(health.IsDown("a"));
        clock.Advance(31);
        Assert.False(health.IsDown("a"));
    }

    [Fact]
    public void Breaker_ModelThatIsMissingOrNotAllowed_IsNeverProbed()
    {
        var clock = new FakeClock();
        var health = new ModelHealth(1, TimeSpan.FromSeconds(60), () => clock.Now);
        health.Failure("a", "404", temporary: false);
        clock.Advance(3600);
        Assert.True(health.IsDown("a"));
    }

    [Fact]
    public void Breaker_InfiniteRetryAfter_KeepsTheOldBehavior()
    {
        var clock = new FakeClock();
        var health = new ModelHealth(1, Timeout.InfiniteTimeSpan, () => clock.Now);
        health.Failure("a", "429");
        clock.Advance(100000);
        Assert.True(health.IsDown("a"));
    }

    [Fact]
    public async Task Chain_ProbesADownModelAfterThePause_AndUsesItAgainWhenItRecovers()
    {
        var clock = new FakeClock();
        var health = new ModelHealth(1, TimeSpan.FromSeconds(60), () => clock.Now);
        var chain = Chain(health);
        bool aWorks = false;
        var calls = new List<string>();
        Task<GenResult> Call(ModelCall c, CancellationToken _)
        {
            calls.Add(c.Model);
            return c.Model == "a" && !aWorks ? throw Err(ErrKind.Quota) : Ok(c.Model);
        }

        await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default);   // a fails, b answers
        calls.Clear();
        await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default);   // a is skipped
        Assert.Equal(new[] { "b" }, calls);

        clock.Advance(61);
        aWorks = true;
        calls.Clear();
        var r = await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default); // the probe
        Assert.Equal("a", r.Model);
        Assert.Equal(new[] { "a" }, calls);
        Assert.False(health.IsDown("a"));
    }

    [Fact]
    public async Task Chain_NotFoundModel_IsNotProbedAgain()
    {
        var clock = new FakeClock();
        var health = new ModelHealth(1, TimeSpan.FromSeconds(60), () => clock.Now);
        var chain = Chain(health);
        var calls = new List<string>();
        Task<GenResult> Call(ModelCall c, CancellationToken _) { calls.Add(c.Model); return c.Model == "a" ? throw Err(ErrKind.NotFound) : Ok(c.Model); }
        await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default);
        clock.Advance(3600);
        calls.Clear();
        await chain.RunAsync(new[] { "a", "b" }, null, Call, 4, default);
        Assert.Equal(new[] { "b" }, calls);
    }

    [Fact]
    public async Task Discovery_IsLoadedOnlyWhenTheListedModelsAreUsedUp()
    {
        int discoveries = 0;
        Task<IEnumerable<string>> More(CancellationToken _) { discoveries++; return Task.FromResult<IEnumerable<string>>(new[] { "a", "found" }); }

        var r1 = await Chain().RunAsync(new[] { "a" }, More, (c, _) => Ok(c.Model), 4, default);
        Assert.Equal(0, discoveries);
        Assert.Equal("a", r1.Model);

        var r2 = await Chain().RunAsync(new[] { "a" }, More, (c, _) => c.Model == "a" ? throw Err(ErrKind.Overloaded) : Ok(c.Model), 4, default);
        Assert.Equal(1, discoveries);
        Assert.Equal("found", r2.Model); // "a" from the discovery list is not tried twice
    }

    [Fact]
    public async Task MaxModels_LimitsHowManyAreTried()
    {
        var calls = new List<string>();
        await Assert.ThrowsAsync<AppException>(() => Chain().RunAsync(new[] { "a", "b", "c", "d" }, null,
            (c, _) => { calls.Add(c.Model); throw Err(ErrKind.Overloaded); }, 2, default));
        Assert.Equal(new[] { "a", "b" }, calls);
    }

    [Fact]
    public async Task AttemptsHint_IsShortWhenAnotherModelIsReady_AndLongForTheLastOne()
    {
        var seen = new List<ModelCall>();
        await Assert.ThrowsAsync<AppException>(() => Chain().RunAsync(new[] { "a", "b" }, null,
            (c, _) => { seen.Add(c); throw Err(ErrKind.Overloaded); }, 4, default));
        Assert.Equal(new ModelCall("a", 3, true), seen[0]);
        Assert.Equal(new ModelCall("b", 5, false), seen[1]);

        seen.Clear();
        await Chain().RunAsync(new[] { "only" }, null, (c, _) => { seen.Add(c); return Ok(c.Model); }, 4, default);
        Assert.Equal(new ModelCall("only", 5, false), seen[0]);
    }

    [Fact]
    public async Task Cancellation_IsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Chain().RunAsync(new[] { "a" }, null, (c, _) => Ok("a"), 4, cts.Token));
    }

    [Fact]
    public async Task ParallelChunks_ShareTheBreakerSafely()
    {
        var health = new ModelHealth(3);
        var chain = Chain(health);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => chain.RunAsync(new[] { "a", "b" }, null,
            async (c, _) => { await Task.Yield(); return c.Model == "a" ? throw Err(ErrKind.Overloaded) : new GenResult("ok", "STOP"); }, 4, default)));
        Assert.All(results, r => Assert.Equal("b", r.Model));
        Assert.True(health.IsDown("a"));
    }

    // ------------------------------------------------------------ wrappers for Claude / OpenAI

    sealed class StubClient : IModelClient
    {
        readonly Func<GenResult> _f;
        public StubClient(Func<GenResult> f) => _f = f;
        public Task<GenResult> GenerateAsync(JsonArray parts, JsonObject generationConfig, bool disableThinkingWhenPossible, CancellationToken ct) => Task.FromResult(_f());
    }

    [Fact]
    public async Task FallbackModelClient_StaysWithinTheProvider_AndNamesTheModelThatAnswered()
    {
        var attempts = new List<(string, int)>();
        var client = new FallbackModelClient("Claude", new[] { "big", "small" }, (model, n) =>
        {
            attempts.Add((model, n));
            return new StubClient(() => model == "big" ? throw new AppException(ErrKind.Overloaded, "busy") { Provider = "Claude" } : new GenResult("hi", "STOP"));
        });
        var r = await client.GenerateAsync(new JsonArray(), new JsonObject(), false, default);
        Assert.Equal("small", r.Model);
        Assert.Equal(new[] { ("big", 3), ("small", 5) }, attempts);
    }

    [Fact]
    public async Task FallbackModelClient_AllModelsFail_ReportsTheProviderError()
    {
        var client = new FallbackModelClient("Claude", new[] { "big", "small" },
            (model, n) => new StubClient(() => throw new AppException(ErrKind.Overloaded, "busy") { Provider = "Claude" }));
        var ex = await Assert.ThrowsAsync<AppException>(() => client.GenerateAsync(new JsonArray(), new JsonObject(), false, default));
        Assert.Equal("Claude", ex.Provider);
        Assert.Equal(ErrKind.Overloaded, ex.Kind);
    }

    [Fact]
    public async Task FallbackTranscriber_FallsBackBetweenTranscriptionModels()
    {
        var used = new List<string>();
        var t = new FallbackTranscriber("OpenAI", new[] { "t1", "t2", "t3" }, (model, n) => (path, ct) =>
        {
            used.Add(model);
            return model == "t3" ? Task.FromResult(new GenResult("text", "STOP")) : throw new AppException(ErrKind.NotFound, "gone") { Provider = "OpenAI" };
        });
        var r = await t.TranscribeAsync("x.wav", default);
        Assert.Equal("t3", r.Model);
        Assert.Equal(new[] { "t1", "t2", "t3" }, used);
    }

    // ------------------------------------------------------------ notes in the result page

    [Fact]
    public void ModelNotes_OneModel_NamesIt_ManyModels_Warn()
    {
        Assert.Empty(Jobs.ModelNotes(Array.Empty<string>()));
        Assert.Equal("הדגם שתמלל: m1.", Assert.Single(Jobs.ModelNotes(new[] { "m1", "m1" })));
        var many = Assert.Single(Jobs.ModelNotes(new[] { "m1", "m2", "m1" }));
        Assert.Contains("m1 (2 קטעים)", many);
        Assert.Contains("m2 (1 קטעים)", many);
        Assert.Contains("ייתכנו הבדלים", many);
    }
}

/// <summary>GeminiClient against a scripted server: real request flow, fake answers.</summary>
public class GeminiFallbackTests
{
    sealed class Route : HttpMessageHandler
    {
        public readonly List<string> Urls = new();
        public Func<string, (HttpStatusCode, string)> Answer = _ => (HttpStatusCode.OK, "{}");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Urls) Urls.Add(url);
            var (code, body) = Answer(url);
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
        public int Count(string model) { lock (Urls) return Urls.Count(u => u.Contains($"/models/{model}:generateContent")); }
    }

    const string OkBody = """{"candidates":[{"content":{"parts":[{"text":"שלום"}]},"finishReason":"STOP"}]}""";
    const string Busy = """{"error":{"message":"The model is overloaded","status":"UNAVAILABLE"}}""";

    static GeminiClient Client(Route route, Action<AppConfig>? tweak = null)
    {
        var cfg = new AppConfig { ApiKey = "k", Model = "m-a", FallbackModel = "m-b" };
        tweak?.Invoke(cfg);
        return new GeminiClient(cfg, null, new HttpClient(route)) { Delay = (_, _) => Task.CompletedTask };
    }

    static Task<GenResult> Gen(GeminiClient c) => c.GenerateAsync(new JsonArray { new JsonObject { ["text"] = "x" } }, new JsonObject(), true, default);

    [Fact]
    public async Task Overloaded_RetriesThenSwitchesToTheFallbackModel_AndReportsIt()
    {
        var route = new Route { Answer = u => u.Contains("m-a:") ? (HttpStatusCode.ServiceUnavailable, Busy) : (HttpStatusCode.OK, OkBody) };
        var r = await Gen(Client(route));
        Assert.Equal("m-b", r.Model);
        Assert.Equal(3, route.Count("m-a"));
        Assert.Equal(1, route.Count("m-b"));
    }

    [Fact]
    public async Task Breaker_StopsSendingRequestsToADeadModel_AfterThreeFailedChunks()
    {
        var route = new Route { Answer = u => u.Contains("m-a:") ? (HttpStatusCode.ServiceUnavailable, Busy) : (HttpStatusCode.OK, OkBody) };
        var client = Client(route);
        for (int i = 0; i < 3; i++) await Gen(client);
        Assert.Equal(9, route.Count("m-a"));
        await Gen(client);
        await Gen(client);
        Assert.Equal(9, route.Count("m-a")); // later chunks went straight to m-b
        Assert.Equal(5, route.Count("m-b"));
    }

    [Fact]
    public async Task RateLimit_SwitchesAfterOneShortRetry()
    {
        var rate = """{"error":{"message":"Resource exhausted: rate limit","status":"RESOURCE_EXHAUSTED"}}""";
        var route = new Route { Answer = u => u.Contains("m-a:") ? ((HttpStatusCode)429, rate) : (HttpStatusCode.OK, OkBody) };
        var r = await Gen(Client(route));
        Assert.Equal("m-b", r.Model);
        Assert.Equal(2, route.Count("m-a"));
    }

    [Fact]
    public async Task ModelNotAllowedForTheKey_403_FallsBack_ButALeakedKeyFailsTheTask()
    {
        var denied = """{"error":{"message":"The caller does not have permission for this model","status":"PERMISSION_DENIED"}}""";
        var route = new Route { Answer = u => u.Contains("m-a:") ? (HttpStatusCode.Forbidden, denied) : (HttpStatusCode.OK, OkBody) };
        Assert.Equal("m-b", (await Gen(Client(route))).Model);

        var leaked = """{"error":{"message":"Your API key was reported as leaked.","status":"PERMISSION_DENIED"}}""";
        var route2 = new Route { Answer = _ => (HttpStatusCode.Forbidden, leaked) };
        var ex = await Assert.ThrowsAsync<AppException>(() => Gen(Client(route2)));
        Assert.Equal(ErrKind.BadKey, ex.Kind);
        Assert.Equal(0, route2.Count("m-b"));
    }

    [Fact]
    public async Task FallbackOff_UsesOnlyTheChosenModel_WithTheFullRetries()
    {
        var route = new Route { Answer = _ => (HttpStatusCode.ServiceUnavailable, Busy) };
        var ex = await Assert.ThrowsAsync<AppException>(() => Gen(Client(route, c => c.AutoFallback = false)));
        Assert.Equal(ErrKind.Overloaded, ex.Kind);
        Assert.Equal(5, route.Count("m-a"));
        Assert.Equal(0, route.Count("m-b"));
        Assert.DoesNotContain(route.Urls, u => u.Contains("/v1beta/models?"));
    }

    [Fact]
    public async Task ConfiguredList_IsUsedInOrder_BeforeDiscovery()
    {
        var route = new Route
        {
            Answer = u => u.Contains("m-a:") || u.Contains("m-x:") ? (HttpStatusCode.ServiceUnavailable, Busy) : (HttpStatusCode.OK, OkBody),
        };
        var r = await Gen(Client(route, c => { c.GeminiModels = "m-x, m-y"; c.FallbackModel = ""; }));
        Assert.Equal("m-y", r.Model);
        Assert.DoesNotContain(route.Urls, u => u.Contains("/v1beta/models?"));
    }

    [Fact]
    public async Task Discovery_IsUsedWhenTheListedModelsAreAllDown()
    {
        var models = """{"models":[{"name":"models/gemini-9-flash","supportedGenerationMethods":["generateContent"]}]}""";
        var route = new Route
        {
            Answer = u => u.Contains("/v1beta/models?") ? (HttpStatusCode.OK, models)
                : u.Contains("gemini-9-flash:") ? (HttpStatusCode.OK, OkBody) : (HttpStatusCode.ServiceUnavailable, Busy),
        };
        var r = await Gen(Client(route));
        Assert.Equal("gemini-9-flash", r.Model);
    }

    [Fact]
    public async Task NoInternet_EndsAsNetworkError_AfterBoundedAttempts()
    {
        var route = new Route { Answer = _ => throw new HttpRequestException("no route") };
        var ex = await Assert.ThrowsAsync<AppException>(() => Gen(Client(route, c => c.GeminiModels = "")));
        Assert.Equal(ErrKind.Network, ex.Kind);
        Assert.True(route.Urls.Count <= 4 * 5, $"too many requests: {route.Urls.Count}");
    }
}
