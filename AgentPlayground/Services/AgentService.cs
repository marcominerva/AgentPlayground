using System.Runtime.CompilerServices;
using AgentPlayground.Models;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

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

                    default:
                        if (!string.IsNullOrEmpty(update.Text))
                        {
                            yield return new(question.ConversationId, update.Text, StreamState.Answering);
                        }

                        break;
                }
            }
        }

        await sessionStore.SaveSessionAsync(agent, new(question.ConversationId.ToString()), session, cancellationToken);
        var response = updates.ToAgentResponse();

        yield return new(question.ConversationId, null, StreamState.Completed, response.Usage);
    }
}
