using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Mime;
using AgentPlayground.Components;
using AgentPlayground.Services;
using AgentPlayground.Settings;
using AgentPlayground.Tools;
using AgentPlayground.Tracing;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using TinyHelpers.AspNetCore.Extensions;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// Add services to the container.
var aiSettings = builder.Services.ConfigureAndGet<AzureOpenAISettings>(builder.Configuration, "AzureOpenAI")!;
var appSettings = builder.Services.ConfigureAndGet<AppSettings>(builder.Configuration, nameof(AppSettings))!;

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.ConfigureHttpClientDefaults(configure =>
{
    configure.AddStandardResilienceHandler(options =>
    {
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
    });
});

builder.Services.AddChatClient(_ =>
{
    var openAIClientOptions = new OpenAIClientOptions
    {
        Endpoint = new(aiSettings.Endpoint),
        Transport = new HttpClientPipelineTransport(new HttpClient(new TraceHttpClientHandler()))
    };
    openAIClientOptions.AddPolicy(new ImageDeploymentPolicy(aiSettings.ImageDeployment), PipelinePosition.PerCall);

    var chatClient = new OpenAIClient(new ApiKeyCredential(aiSettings.ApiKey), openAIClientOptions)
        .GetResponsesClient().AsIChatClientWithStoredOutputDisabled(aiSettings.Deployment);

    return chatClient;
});

builder.Services.AddHybridCache(options =>
{
    options.DefaultEntryOptions = new()
    {
        LocalCacheExpiration = appSettings.MessageExpiration
    };
});
builder.Services.AddSingleton<HybridCacheSessionStoreService>();

builder.Services.AddAIAgent("PlaygroundAgent", (services, key) =>
{
    var chatClient = services.GetRequiredService<IChatClient>();

    return chatClient.AsAIAgent(new()
    {
        Id = key.ToLowerInvariant(),
        Name = key,
        ChatOptions = new()
        {
            Instructions = """
                You are a helpful assistant. Answer the user's questions in the same language as the question.
                """,
            Reasoning = new()
            {
                Effort = ReasoningEffort.Low,
                Output = ReasoningOutput.Summary
            },
            Tools = [new HostedWebSearchTool(),
                new HostedImageGenerationTool
                {
                    Options = new()
                    {
                        ModelId = aiSettings.ImageDeployment,
                        Count = 1,
                        ImageSize = new(1536,1024),
                        MediaType = MediaTypeNames.Image.Png,
                        StreamingCount = 3
                    }
                },
                AIFunctionFactory.Create(DateTimeTools.GetCurrentDateTime)]
        },
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new()
        {
            ChatReducer = new MessageCountingChatReducer(appSettings.MessageLimit),
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded
        })
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
}, ServiceLifetime.Scoped)
//.WithSessionStore((services, _) => services.GetRequiredService<HybridCacheSessionStoreService>(), withIsolation: false)
.WithInMemorySessionStore(withIsolation: false)
;

builder.Services.AddScoped<AgentService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseHttpsRedirection();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);

    // The default HSTS value is 30 days.
    app.UseHsts();
}

app.UseStatusCodePagesWithRedirects("/error?code={0}");

app.UseRouting();
app.UseRequestLocalization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

internal class ImageDeploymentPolicy(string deployment) : PipelinePolicy
{
    private const string HeaderName = "x-ms-oai-image-generation-deployment";

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        message.Request.Headers.Set(HeaderName, deployment);
        ProcessNext(message, pipeline, index);
    }

    public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
    {
        message.Request.Headers.Set(HeaderName, deployment);
        return ProcessNextAsync(message, pipeline, index);
    }
}