using System.Net.Mime;
using System.Runtime.CompilerServices;
using AgentPlayground.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace AgentPlayground.Services;

/// <summary>
/// Runs the agent registered in <c>Program.cs</c> with the name <c>PlaygroundAgent</c>, keeping the conversation
/// history in the corresponding <see cref="AgentSessionStore"/>.
/// </summary>
public class AgentService([FromKeyedServices("PlaygroundAgent")] AIAgent agent, [FromKeyedServices("PlaygroundAgent")] AgentSessionStore sessionStore)
{
    /// <summary>
    /// Asks the agent a question and streams the answer back, ending with a message that contains the token usage.
    /// </summary>
    /// <param name="question">The question, along with the identifier of the conversation it belongs to.</param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.</param>
    /// <returns>The stream of <see cref="Response"/> objects produced by the agent.</returns>
    public async IAsyncEnumerable<Response> AskStreamingAsync(Question question, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var session = await sessionStore.GetOrCreateSessionAsync(agent, new(question.ConversationId.ToString()), cancellationToken);

        var updates = new List<AgentResponseUpdate>();

        await foreach (var update in agent.RunStreamingAsync(question.Text, session, cancellationToken: cancellationToken))
        {
            updates.Add(update);

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextReasoningContent textReasoningContent when !string.IsNullOrEmpty(textReasoningContent.Text):
                        yield return new(question.ConversationId, textReasoningContent.Text, StreamState.Reasoning);
                        break;

                    case FunctionCallContent functionCallContent:
                        yield return new(question.ConversationId, $"{functionCallContent.Name}({string.Join(", ", functionCallContent.Arguments?.Select(a => $"{a.Key} = {a.Value}") ?? [])})", StreamState.FunctionCalling);
                        break;

                    case ImageGenerationToolResultContent imageGenerationContent:
                        if (imageGenerationContent.Outputs is not null)
                        {
                            foreach (var output in imageGenerationContent.Outputs.OfType<DataContent>())
                            {
                                yield return new(question.ConversationId, output.Uri, StreamState.ImageGeneration);
                            }
                        }

                        break;

                    default:
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            yield return new(question.ConversationId, update.Text, StreamState.Answering);
                        }

                        break;
                }
            }

            var finalImage = GetFinalImage(update);
            if (finalImage is not null)
            {
                yield return new(question.ConversationId, finalImage.Uri, StreamState.ImageGeneration);
            }
        }

        await sessionStore.SaveSessionAsync(agent, new(question.ConversationId.ToString()), session, cancellationToken);
        var response = updates.ToAgentResponse();

        yield return new(question.ConversationId, null, StreamState.Completed, response.Usage);

        static DataContent? GetFinalImage(AgentResponseUpdate update)
        {
            for (var raw = update.RawRepresentation; raw is not null; raw = (raw as ChatResponseUpdate)?.RawRepresentation)
            {
                if (raw is StreamingResponseOutputItemDoneUpdate { Item: ImageGenerationCallResponseItem image })
                {
                    var mediaType = image.OutputFileFormat.HasValue ?
                        $"image/{image.OutputFileFormat.Value}" : MediaTypeNames.Image.Png;

                    var dataContent = new DataContent(image.ImageResultBytes, mediaType);
                    return dataContent;
                }
            }

            return null;
        }
    }
}
