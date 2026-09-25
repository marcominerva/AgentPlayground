using System.ClientModel;
using System.ClientModel.Primitives;
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
    var chatClient = new OpenAIClient(new ApiKeyCredential(aiSettings.ApiKey), new()
    {
        Endpoint = new(aiSettings.Endpoint),
        Transport = new HttpClientPipelineTransport(new(new TraceHttpClientHandler()))
    }).GetResponsesClient().AsIChatClientWithStoredOutputDisabled(aiSettings.Deployment);

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
            Tools = [new HostedWebSearchTool(), AIFunctionFactory.Create(DateTimeTools.GetCurrentDateTime)]
        },
        ChatHistoryProvider = new InMemoryChatHistoryProvider(new()
        {
            ChatReducer = new MessageCountingChatReducer(appSettings.MessageLimit),
            ReducerTriggerEvent = InMemoryChatHistoryProviderOptions.ChatReducerTriggerEvent.AfterMessageAdded,
            StorageInputRequestMessageFilter = messages =>
            {
                // The messages list contains the request messages of the current turn, but it does not contain the response messages yet,
                // as we are still in the process of handling the request.
                // This method is called AFTER the response is received from the LLM, but before storing the response messages in the chat history,
                // so we can filter out request messages that we don't want to store.
                // For example, we can filter out messages from the AI Context Providers, as they can be re-generated if needed.
                // By default the chat history provider will store all messages, except for those that came from chat history in the first place.
                return messages.Where(m => m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.ChatHistory
                    && m.GetAgentRequestMessageSourceType() != AgentRequestMessageSourceType.AIContextProvider);
            }
        })
    },
    loggerFactory: services.GetRequiredService<ILoggerFactory>(),
    services: services);
}, ServiceLifetime.Scoped)
.WithSessionStore((services, _) => services.GetRequiredService<HybridCacheSessionStoreService>(), withIsolation: false);

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