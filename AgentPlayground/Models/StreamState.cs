namespace AgentPlayground.Models;

public enum StreamState
{
    Reasoning,
    FunctionCalling,
    ImageGeneration,
    Answering,
    ImageGeneration,
    Completed
}