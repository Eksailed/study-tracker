using System.Text.Json.Nodes;

namespace StudyTracker.Tests.Support;

public static class ApiClientExtensions
{
    public static async Task<int> CreateSubject(this ApiClient client, string name, string color = "blue")
    {
        var response = await client.Post("subjects", new { name, color });
        Assert.True(response.Status == 201, "Предмет не создан: " + response.Body);
        return response.Body.GetProperty("id").GetInt32();
    }

    /// <summary>Создаёт задание «Лабораторная № 2» со сроком 2026-11-12; поля можно переопределить.</summary>
    public static async Task<JsonObject> CreateTask(this ApiClient client, int subjectId, JsonObject? overrides = null)
    {
        var data = new JsonObject
        {
            ["title"] = "Лабораторная № 2", ["description"] = "Проверить запросы", ["subjectId"] = subjectId,
            ["dueDate"] = "2026-11-12", ["status"] = "Todo",
        };
        foreach (var (key, value) in overrides ?? []) data[key] = value?.DeepClone();
        var response = await client.Post("assignments", data);
        Assert.True(response.Status == 201, "Задание не создано: " + response.Body);
        return JsonNode.Parse(response.Body.GetRawText())!.AsObject();
    }
}
