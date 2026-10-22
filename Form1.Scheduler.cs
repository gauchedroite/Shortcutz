using System.Text;
using System.Text.Json;

namespace Shortcutz;

public partial class Form1
{
    // shortcut: API base URL hardcoded; remember-the-milk's config.json sets the port (default 8080).
    private const string SchedulerApi = "http://localhost:8181";
    // Parsing goes through an LLM and can take well over the shared _http's 5s timeout.
    private static readonly HttpClient _schedulerHttp = new() { Timeout = TimeSpan.FromMinutes(2) };

    private async void SchedulerAddTask()
    {
        var query = Prompt("Add scheduled task", "Task (single line):", "");
        if (string.IsNullOrWhiteSpace(query)) return;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
            using var response = await _schedulerHttp.PostAsync($"{SchedulerApi}/tasks", content);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                MessageBox.Show(this, body, "Could not add task", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var task = JsonSerializer.Deserialize<JsonElement>(body);
            var description = task.TryGetProperty("description", out var d) ? d.GetString() : query;
            var next = task.TryGetProperty("nextOccurrence", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
            MessageBox.Show(this, $"Scheduled: {description}\nNext: {next}", "Task added", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not add task", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void SchedulerListTasks()
    {
        try
        {
            var body = await _schedulerHttp.GetStringAsync($"{SchedulerApi}/tasks");
            var tasks = JsonSerializer.Deserialize<JsonElement>(body);
            var lines = new StringBuilder();
            foreach (var task in tasks.EnumerateArray())
            {
                var description = task.TryGetProperty("description", out var d) ? d.GetString() : "";
                var type = task.TryGetProperty("scheduleType", out var t) ? t.GetString() : "";
                var next = task.TryGetProperty("nextOccurrence", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
                lines.AppendLine($"{next,-20} {type,-8} {description}");
            }
            MessageBox.Show(this,
                lines.Length == 0 ? "No scheduled tasks." : lines.ToString(),
                "Scheduled tasks", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not list tasks", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
