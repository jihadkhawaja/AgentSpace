using System.Text.Json;

namespace AgentSpace;

public interface ILongTermTransaction
{
    IReadOnlyDictionary<string, JsonElement> ReadCollection(string collection);
    void Set(string collection, string key, JsonElement? value);
}
